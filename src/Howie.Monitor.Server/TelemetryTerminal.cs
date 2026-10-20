namespace Howie.Monitor.Server;

public sealed partial class TelemetryStore
{
    public (string DeviceId, string AgentSessionId)? AuthorizeTerminal(string? authorization, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open();
            var actor = Authorize(db, authorization, now);
            // 尚未激活的安装凭据只能验证采集，不能领取终端会话。
            if (actor is null || actor.Candidate) return null;
            var registration = Registration(db, actor.DeviceId);
            return registration is null ? null : (actor.DeviceId, registration.SessionId);
        }
    }
}
