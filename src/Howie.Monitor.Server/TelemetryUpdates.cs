using System.Text.Json;
using Howie.Monitor.Contracts;
using Microsoft.Data.Sqlite;

namespace Howie.Monitor.Server;

public sealed partial class TelemetryStore
{
    private static AgentUpdateOperation? ReadUpdate(SqliteConnection db, string id) => ReadJson<AgentUpdateOperation>(db,
        "SELECT payload FROM agent_updates WHERE device=$device", null, ("$device", id));
    private static void SaveUpdate(SqliteConnection db, AgentUpdateOperation operation) => Execute(db, null,
        "INSERT INTO agent_updates VALUES($device,$json) ON CONFLICT(device) DO UPDATE SET payload=$json",
        ("$device", operation.Ticket.DeviceId), ("$json", JsonSerializer.Serialize(operation, ProtocolJson.Options)));

    public bool HasActiveUpdate(string id)
    {
        var operation = UpdateStatus(id, null, DateTimeOffset.UtcNow)?.Operation;
        return operation is not null && !AgentUpdatePhases.Finished(operation.Phase);
    }
    public AgentUpdateStatus? UpdateStatus(string id, AgentRelease? release, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open(); var device = Registration(db, id); if (device is null) return null;
            var operation = ReadUpdate(db, id);
            if (operation is { Phase: "queued" or "downloading" } && (now > operation.Ticket.ExpiresAt || device.SessionId != operation.Ticket.SourceSessionId))
            { operation = operation with { Phase = "failed", Message = "任务过期或采集会话已变化，请重新确认。", UpdatedAt = now }; SaveUpdate(db, operation); }
            string? reason = operation is not null && !AgentUpdatePhases.Finished(operation.Phase)
                ? operation.Phase == "recoveryRequired" ? "请在本机运行新版安装包修复，恢复上报后再操作。"
                : now > operation.Ticket.ExpiresAt ? "升级结果待确认，请保持电脑联网；不要重复安装。必要时在本机运行修复。" : "这台电脑已有升级任务。"
                : device.Build is not { IsRelease: true } || device.Updates?.Protocol != 1 ? "请先在本机安装支持面板升级的新版采集端。"
                : !device.Updates.Enabled ? "请在本机安装向导或 CLI 中开启“允许面板升级”。"
                : release is null ? "服务器尚未发布带完整版本清单的安装包。"
                : device.Build.BuildId == release.BuildId ? "已安装当前发布版本。"
                : System.Version.Parse(release.Version) <= System.Version.Parse(device.Build.Version) ? "仅支持升级至更高版本，不允许远程降级或覆盖同版本构建。"
                : ReadLatest(db, id, now) is not { ConnectionStatus: "online", DataStatus: "fresh" } ? "电脑需要在线且正在上报数据。" : null;
            return new(id, device.Build, device.SessionId, release, reason is null, reason, operation);
        }
    }
    public (AgentUpdateOperation? Operation, string? Error) CreateUpdate(AgentUpdateRequest request, AgentRelease? release, DateTimeOffset now)
    {
        lock (_gate)
        {
            var status = UpdateStatus(request.ComputerId, release, now);
            if (status is null) return (null, "目标电脑不存在。");
            if (!status.CanUpdate) return (null, status.Reason);
            if (status.SessionId != request.ExpectedSessionId || !string.Equals(release!.Sha256, request.Sha256, StringComparison.OrdinalIgnoreCase))
                return (null, "电脑会话或发布版本已变化，请刷新后重新确认。");
            var operation = new AgentUpdateOperation(new(Guid.NewGuid().ToString("N"), request.ComputerId,
                status.SessionId, status.Current!.BuildId, release, now, now.AddMinutes(20)), "queued", null, now);
            using var db = Open(); SaveUpdate(db, operation); return (operation, null);
        }
    }
    public (bool Authorized, AgentUpdateTicket? Ticket) PollUpdate(string? authorization, string sessionId, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open(); var actor = Authorize(db, authorization, now);
            if (actor is null || actor.Candidate) return (false, null);
            var device = Registration(db, actor.DeviceId); var operation = ReadUpdate(db, actor.DeviceId);
            if (device?.SessionId != sessionId || device.Updates is not { Enabled: true, Protocol: 1 }
                || operation is null || operation.Ticket.SourceSessionId != sessionId || operation.Ticket.ExpiresAt <= now) return (true, null);
            if (operation.Phase is not ("queued" or "downloading")) return (true, null);
            if (operation.Phase == "queued") SaveUpdate(db, operation with { Phase = "downloading", UpdatedAt = now });
            return (true, operation.Ticket);
        }
    }
    private static bool UpdateReady(SqliteConnection db, AgentUpdateTicket ticket, bool rollback, DateTimeOffset now)
    {
        var device = Registration(db, ticket.DeviceId); var view = ReadLatest(db, ticket.DeviceId, now);
        bool build = rollback ? device?.Build?.BuildId == ticket.SourceBuildId
            : device?.Build?.BuildId == ticket.Release.BuildId && device.Build.Version == ticket.Release.Version;
        return build && device!.SessionId != ticket.SourceSessionId && view is { ConnectionStatus: "online", DataStatus: "fresh" }
            && view.ReceivedAt > ticket.CreatedAt && view.LastHeartbeat > ticket.CreatedAt
            && view.Sample?.Metrics.Any(value => value.Key == "cpu.usage" && value.DeviceId == "cpu/total" && value.Unit == "%" && value.Status == MetricStatus.Available) == true
            && view.Sample.Metrics.Any(value => value.Key == "memory.usage" && value.DeviceId == "memory" && value.Unit == "%" && value.Status == MetricStatus.Available);
    }
    public AgentUpdateHealth? UpdateHealth(string? authorization, string taskId, bool rollback, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open(); var actor = Authorize(db, authorization, now);
            if (actor is null || actor.Candidate) return null;
            var operation = ReadUpdate(db, actor.DeviceId);
            return operation?.Ticket.Id == taskId ? new(UpdateReady(db, operation.Ticket, rollback, now)) : null;
        }
    }
    public (bool Authorized, bool Accepted) ReportUpdate(string? authorization, AgentUpdateReport report, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open(); var actor = Authorize(db, authorization, now);
            if (actor is null || actor.Candidate) return (false, false);
            var operation = ReadUpdate(db, actor.DeviceId);
            if (operation?.Ticket.Id != report.Id || report.Message?.Length > 256) return (true, false);
            if (operation.Phase == report.Phase && report.Phase != "installing") return (true, true);
            if (AgentUpdatePhases.Finished(operation.Phase)) return (true, operation.Phase == report.Phase);
            bool allowed = report.Phase switch
            {
                "installing" => operation.Phase is "downloading" or "installing" && now <= operation.Ticket.ExpiresAt
                    && Registration(db, actor.DeviceId) is { Updates.Enabled: true } device
                    && device.SessionId == operation.Ticket.SourceSessionId && device.Build?.BuildId == operation.Ticket.SourceBuildId
                    && ReadLatest(db, actor.DeviceId, now) is { ConnectionStatus: "online", DataStatus: "fresh" },
                "verifying" => operation.Phase is "installing" or "verifying",
                "rollingBack" => operation.Phase is "installing" or "verifying" or "rollingBack",
                "succeeded" => operation.Phase == "verifying" && UpdateReady(db, operation.Ticket, false, now),
                "rolledBack" => operation.Phase == "rollingBack" && UpdateReady(db, operation.Ticket, true, now),
                "failed" => operation.Phase is "queued" or "downloading" || operation.Phase == "installing"
                    && Registration(db, actor.DeviceId)?.Build?.BuildId == operation.Ticket.SourceBuildId
                    && ReadLatest(db, actor.DeviceId, now) is { ConnectionStatus: "online", DataStatus: "fresh" },
                "repaired" => Registration(db, actor.DeviceId)?.SessionId != operation.Ticket.SourceSessionId
                    && ReadLatest(db, actor.DeviceId, now) is { ConnectionStatus: "online", DataStatus: "fresh" },
                "recoveryRequired" => operation.Phase is "installing" or "verifying" or "rollingBack" or "recoveryRequired",
                _ => false
            };
            if (!allowed) return (true, false);
            SaveUpdate(db, operation with { Phase = report.Phase, Message = report.Message, UpdatedAt = now }); return (true, true);
        }
    }
}
