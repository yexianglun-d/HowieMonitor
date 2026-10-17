using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Server;

public sealed class PendingEnrollment
{
    public string CodeHash { get; set; } = "";
    public DateTimeOffset CodeExpiresAt { get; set; }
    public string ExpectedDeviceId { get; set; } = "";
    public string? PreviousComputerName { get; set; }
    public string? RequestId { get; set; }
    public string? ComputerName { get; set; }
    public string? DeviceId { get; set; }
    public string? TokenHash { get; set; }
    public string? ProtectedToken { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool PreservesHistory { get; set; }
    public string State { get; set; } = "code";
    public bool AddComputer { get; set; }
    public DeviceRegistration? Registration { get; set; }
    public Heartbeat? Heartbeat { get; set; }
    public DateTimeOffset? HeartbeatReceived { get; set; }
    public TelemetrySample? Sample { get; set; }
    public DateTimeOffset? SampleReceived { get; set; }
}

public sealed partial class TelemetryStore
{
    private IDataProtector? _enrollmentProtector;
    private sealed record EnrollmentActor(string DeviceId, bool Candidate);

    public void InitializeEnrollment(DeviceCredential initial, IDataProtector protector)
    {
        lock (_gate)
        {
            using var db = Open();
            Execute(db, null, """
                CREATE TABLE IF NOT EXISTS enrollment_active(id INTEGER PRIMARY KEY CHECK(id=1),device TEXT NOT NULL,hash TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS enrollment_pending(id INTEGER PRIMARY KEY CHECK(id=1),payload TEXT NOT NULL);
                """);
            Execute(db, null, "INSERT OR IGNORE INTO enrollment_active VALUES(1,$device,$hash)",
                ("$device", initial.DeviceId), ("$hash", SecretHash(initial.Token)));
            using var transaction = db.BeginTransaction();
            Execute(db, transaction, """
                CREATE TABLE IF NOT EXISTS enrollment_devices(device TEXT PRIMARY KEY,hash TEXT NOT NULL UNIQUE);
                INSERT OR IGNORE INTO enrollment_devices SELECT device,hash FROM enrollment_active
                    WHERE NOT EXISTS(SELECT 1 FROM history_metadata WHERE name='multi-credentials-v1');
                INSERT OR IGNORE INTO history_metadata VALUES('multi-credentials-v1','complete');
                """);
            transaction.Commit();
            _enrollmentProtector = protector;
        }
    }

    public (EnrollmentCode? Code, string? Error) CreateEnrollmentCode(EnrollmentCodeRequest request, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open();
            if (request.AddComputer && request.ConfirmReplacement) return (null, "新增与替换不能同时选择。");
            var device = Registration(db);
            if (!request.AddComputer && device is not null && (!request.ConfirmReplacement || request.ExpectedDeviceId != device.DeviceId))
                return (null, "请确认当前电脑及接入/更换操作，再生成绑定码。");
            string code = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
            var pending = new PendingEnrollment { CodeHash = SecretHash(code), CodeExpiresAt = now.AddMinutes(10),
                AddComputer = request.AddComputer, ExpectedDeviceId = request.AddComputer ? "" : ActiveDevice(db),
                PreviousComputerName = request.AddComputer ? null : device?.Name };
            SavePending(db, null, pending);
            return (new(code.Insert(8, "-").Insert(4, "-"), pending.CodeExpiresAt, pending.PreviousComputerName), null);
        }
    }
    public void RevokeEnrollment() { lock (_gate) { using var db = Open(); Execute(db, null, "DELETE FROM enrollment_pending"); } }

    public (EnrollmentCredential? Credential, string? Error) ClaimEnrollment(EnrollmentClaim claim, DateTimeOffset now)
    {
        if (claim.Code?.Length is not (> 0 and <= 40) || !Guid.TryParse(claim.RequestId, out _)
            || string.IsNullOrWhiteSpace(claim.ComputerName) || claim.ComputerName.Length > 128 || claim.ComputerName.Any(char.IsControl)
            || claim.ExistingToken?.Length > 512 || claim.ExistingDeviceId?.Length > 200) return (null, "绑定码或电脑信息不正确。");
        lock (_gate)
        {
            using var db = Open();
            var pending = Pending(db);
            string code = claim.Code.Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
            if (pending is null || !SameHash(pending.CodeHash, SecretHash(code))) return (null, "绑定码无效或已撤销，请重新生成。");
            if (!pending.AddComputer && pending.ExpectedDeviceId != ActiveDevice(db) && pending.State != "activated") return (null, "当前监控电脑已变更，请重新生成绑定码。");
            if (pending.RequestId is not null)
            {
                if (pending.RequestId != claim.RequestId || pending.ComputerName != claim.ComputerName) return (null, "绑定码已由另一安装请求使用。");
                if (pending.ExpiresAt <= now || pending.ProtectedToken is null) return (null, "接入授权已过期，请重新生成绑定码。");
                return (ClaimCredential(pending), null);
            }
            if (pending.CodeExpiresAt <= now) return (null, "绑定码已过期，请重新生成。");
            string deviceId = "device-" + Guid.NewGuid().ToString("N");
            bool preserve = claim.ExistingDeviceId is not null || claim.ExistingToken is not null;
            if (preserve)
            {
                var actor = Authorize(db, "Bearer " + claim.ExistingToken, now);
                if (actor is null || actor.Candidate || actor.DeviceId != claim.ExistingDeviceId
                    || !string.Equals(claim.ComputerName, Registration(db, actor.DeviceId)?.Name, StringComparison.OrdinalIgnoreCase)
                    || (!pending.AddComputer && actor.DeviceId != pending.ExpectedDeviceId))
                    return (null, "已有配置与当前电脑不一致，不能按原设备接管。");
                deviceId = actor.DeviceId;
                pending.PreviousComputerName = Registration(db, actor.DeviceId)!.Name;
            }
            string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            pending.RequestId = claim.RequestId; pending.ComputerName = claim.ComputerName; pending.DeviceId = deviceId;
            pending.TokenHash = SecretHash(token); pending.ProtectedToken = Protector.Protect(token);
            pending.ExpiresAt = now.AddMinutes(20); pending.PreservesHistory = preserve; pending.State = "claimed";
            SavePending(db, null, pending);
            return (new(deviceId, token, pending.ExpiresAt.Value, pending.PreviousComputerName, preserve), null);
        }
    }

    public bool AuthenticateIngest(string? authorization, DateTimeOffset now)
    { lock (_gate) { using var db = Open(); return Authorize(db, authorization, now) is not null; } }

    public (bool Authorized, string? Error) RegisterAuthenticated(string? authorization, DeviceRegistration device, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open(); var actor = Authorize(db, authorization, now);
            if (actor is null) return (false, null);
            if (device.DeviceId != actor.DeviceId || !Guid.TryParse(device.SessionId, out _) || device.BootId?.Length is not (> 0 and <= 128)
                || device.Name?.Length is not (> 0 and <= 128) || device.Name.Any(char.IsControl) || device.Inventory is null || device.Sensors is null)
                return (true, "设备信息不正确。");
            if (actor.Candidate)
            {
                var pending = Pending(db)!;
                if (device.Name != pending.ComputerName) return (true, "电脑名称与绑定请求不一致。");
                if (pending.Registration?.SessionId != device.SessionId)
                { pending.Sample = null; pending.SampleReceived = null; pending.Heartbeat = null; pending.HeartbeatReceived = null; }
                pending.Registration = device; SavePending(db, null, pending);
            }
            else
            {
                using var transaction = db.BeginTransaction();
                Execute(db, transaction, "INSERT INTO device VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET registration=$json",
                    ("$id", device.DeviceId), ("$json", JsonSerializer.Serialize(device, ProtocolJson.Options)));
                RememberHistoryComputer(db, transaction, device); transaction.Commit();
            }
            return (true, null);
        }
    }

    public (bool Authorized, string? Error) BeatAuthenticated(string? authorization, Heartbeat beat, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open(); var actor = Authorize(db, authorization, now);
            if (actor is null) return (false, null);
            var pending = actor.Candidate ? Pending(db) : null;
            var registration = pending?.Registration ?? (actor.Candidate ? null : Registration(db, actor.DeviceId));
            if (registration is null || beat.DeviceId != actor.DeviceId || beat.SessionId != registration.SessionId || beat.BootId != registration.BootId
                || beat.QueueLength < 0 || beat.DroppedSamples < 0 || beat.LastSequence < 0 || beat.LastError?.Length > 512)
                return (true, "心跳信息不正确或采集会话未注册。");
            if (pending is not null) { pending.Heartbeat = beat; pending.HeartbeatReceived = now; SavePending(db, null, pending); }
            else Execute(db, null, "INSERT INTO computer_heartbeat VALUES($device,$now,$json) ON CONFLICT(device) DO UPDATE SET received=$now,payload=$json",
                ("$device", actor.DeviceId), ("$now", now.ToUnixTimeMilliseconds()), ("$json", JsonSerializer.Serialize(beat, ProtocolJson.Options)));
            return (true, null);
        }
    }

    public (bool Authorized, string? Error, IngestAcknowledgement? Acknowledgement) IngestAuthenticated(string? authorization, IngestRequest request, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open(); var actor = Authorize(db, authorization, now);
            if (actor is null) return (false, null, null);
            string? error = TelemetryValidation.Validate(request, actor.DeviceId, now);
            if (error is not null) return (true, error, null);
            if (actor.Candidate)
            {
                var pending = Pending(db)!;
                if (pending.Registration is null) return (true, "请先注册电脑。", null);
                if (!request.Backfill)
                {
                    var sample = request.Samples.Where(value => value.SessionId == pending.Registration.SessionId && value.BootId == pending.Registration.BootId)
                        .OrderByDescending(value => value.Sequence).FirstOrDefault();
                    if (sample is not null && (pending.Sample is null || sample.Sequence > pending.Sample.Sequence))
                    { pending.Sample = sample; pending.SampleReceived = now; SavePending(db, null, pending); }
                }
                return (true, null, new(request.Samples.Select(value => new SampleIdentity(value.SessionId, value.Sequence)).ToArray()));
            }
            using var transaction = db.BeginTransaction();
            var acknowledged = IngestCore(db, transaction, request, now); transaction.Commit();
            return (true, null, acknowledged);
        }
    }

    public EnrollmentProgress? EnrollmentStatus(string? authorization, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open(); var actor = Authorize(db, authorization, now);
            if (actor is null) return null;
            return actor.Candidate ? Progress(Pending(db)!, now) : ActiveProgress(db, actor.DeviceId, now);
        }
    }
    public EnrollmentOverview EnrollmentOverview(DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open(); var device = Registration(db); var pending = Pending(db);
            return new(device?.DeviceId, device?.Name, pending is null ? null : pending.State == "activated"
                ? ActiveProgress(db, pending.DeviceId!, now) : Progress(pending, now), pending?.CodeExpiresAt);
        }
    }

    public (bool Authorized, string? Error, EnrollmentProgress? Progress) ActivateEnrollment(string? authorization, string requestId, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open(); var actor = Authorize(db, authorization, now);
            if (actor is null) return (false, null, null);
            var pending = Pending(db);
            if (pending is null || pending.RequestId != requestId || pending.DeviceId != actor.DeviceId)
                return (true, "接入请求身份不正确。", null);
            if (!actor.Candidate && pending.State == "activated") return (true, null, ActiveProgress(db, actor.DeviceId, now));
            if (!actor.Candidate || Progress(pending, now).State != "ready") return (true, "安装接入尚未收到新鲜的 CPU、内存和心跳，不能激活。", null);
            if (!pending.AddComputer && pending.ExpectedDeviceId != ActiveDevice(db)) return (true, "当前电脑已变更，请重新绑定。", null);
            using var transaction = db.BeginTransaction();
            if (!pending.AddComputer)
            {
                Execute(db, transaction, "UPDATE enrollment_active SET device=$device,hash=$hash WHERE id=1", ("$device", pending.DeviceId!), ("$hash", pending.TokenHash!));
                Execute(db, transaction, "DELETE FROM enrollment_devices WHERE device=$old; DELETE FROM device WHERE id=$old; DELETE FROM computer_latest WHERE device=$old; DELETE FROM computer_heartbeat WHERE device=$old;",
                    ("$old", pending.ExpectedDeviceId));
            }
            // 新增和同设备接管只写本机凭据及状态，不撤销其他电脑。
            Execute(db, transaction, "INSERT INTO enrollment_devices VALUES($device,$hash) ON CONFLICT(device) DO UPDATE SET hash=$hash",
                ("$device", pending.DeviceId!), ("$hash", pending.TokenHash!));
            Execute(db, transaction, "INSERT INTO device VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET registration=$json", ("$id", pending.DeviceId!), ("$json", JsonSerializer.Serialize(pending.Registration, ProtocolJson.Options)));
            RememberHistoryComputer(db, transaction, pending.Registration!);
            Execute(db, transaction, "INSERT INTO computer_heartbeat VALUES($device,$now,$json) ON CONFLICT(device) DO UPDATE SET received=$now,payload=$json", ("$device", pending.DeviceId!), ("$now", pending.HeartbeatReceived!.Value.ToUnixTimeMilliseconds()),
                ("$json", JsonSerializer.Serialize(pending.Heartbeat, ProtocolJson.Options)));
            IngestCore(db, transaction, new(false, [pending.Sample!]), pending.SampleReceived!.Value);
            pending.State = "activated"; pending.Registration = null; pending.Sample = null; pending.Heartbeat = null;
            SavePending(db, transaction, pending); transaction.Commit();
            return (true, null, ActiveProgress(db, actor.DeviceId, now));
        }
    }

    private IDataProtector Protector => _enrollmentProtector ?? throw new InvalidOperationException("接入协议尚未初始化。");
    private EnrollmentCredential ClaimCredential(PendingEnrollment pending) => new(pending.DeviceId!, Protector.Unprotect(pending.ProtectedToken!),
        pending.ExpiresAt!.Value, pending.PreviousComputerName, pending.PreservesHistory);
    private static string SecretHash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    private static bool SameHash(string expected, string actual) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));
    private static string ActiveDevice(SqliteConnection db)
    { using var command = db.CreateCommand(); command.CommandText = "SELECT device FROM enrollment_active WHERE id=1"; return (string)command.ExecuteScalar()!; }
    private static PendingEnrollment? Pending(SqliteConnection db) => ReadJson<PendingEnrollment>(db, "SELECT payload FROM enrollment_pending WHERE id=1");
    private static void SavePending(SqliteConnection db, SqliteTransaction? transaction, PendingEnrollment pending)
        => Execute(db, transaction, "INSERT INTO enrollment_pending VALUES(1,$json) ON CONFLICT(id) DO UPDATE SET payload=$json",
            ("$json", JsonSerializer.Serialize(pending, ProtocolJson.Options)));
    private static EnrollmentActor? Authorize(SqliteConnection db, string? authorization, DateTimeOffset now)
    {
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal) || authorization.Length > 519) return null;
        string hash = SecretHash(authorization[7..]);
        using var command = db.CreateCommand(); command.CommandText = "SELECT device,hash FROM enrollment_devices";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (SameHash(reader.GetString(1), hash)) return new(reader.GetString(0), false);
        reader.Close();
        var pending = Pending(db);
        return pending is { State: "claimed", DeviceId: not null, TokenHash: not null } && pending.ExpiresAt > now
            && SameHash(pending.TokenHash, hash) ? new(pending.DeviceId, true) : null;
    }
    private static EnrollmentProgress Progress(PendingEnrollment pending, DateTimeOffset now)
    {
        string status = DataStatus(pending.Sample, pending.Registration?.SessionId, now);
        bool heartbeat = pending.HeartbeatReceived.HasValue && now - pending.HeartbeatReceived <= TimeSpan.FromSeconds(30)
            && pending.Heartbeat?.SessionId == pending.Registration?.SessionId;
        bool basis = pending.Sample?.Metrics.Any(value => value.Key == "cpu.usage" && value.DeviceId == "cpu/total" && value.Unit == "%" && value.Status == MetricStatus.Available) == true
            && pending.Sample.Metrics.Any(value => value.Key == "memory.usage" && value.DeviceId == "memory" && value.Unit == "%" && value.Status == MetricStatus.Available);
        string state = pending.State == "code" ? pending.CodeExpiresAt > now ? "code" : "expired"
            : pending.ExpiresAt <= now ? "expired" : pending.State == "activated" ? "active" : status == "fresh" && heartbeat && basis ? "ready" : "waiting";
        return new(state, pending.ComputerName, pending.Registration is not null, heartbeat, status, pending.Sample?.SampledAt,
            state == "expired" ? "接入授权已过期，请重新绑定。" : status == "clockError" ? "电脑时钟异常，请校时后重试。"
            : pending.Sample is not null && !basis ? "等待可用的 CPU 与内存基础指标。" : null);
    }
    private static EnrollmentProgress ActiveProgress(SqliteConnection db, string deviceId, DateTimeOffset now)
    {
        var device = Registration(db, deviceId); var view = ReadLatest(db, deviceId, now);
        return new("active", device?.Name, device is not null, view.ConnectionStatus == "online", view.DataStatus, view.Sample?.SampledAt, null);
    }
}
