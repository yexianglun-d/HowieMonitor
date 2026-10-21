using System.Reflection;

namespace Howie.Monitor.Contracts;

public sealed record AgentUpdateAccess(bool Enabled);
public sealed record AgentBuild(string Version, string BuildId, int UpdateProtocol)
{
    public static AgentBuild Current { get; } = new(Metadata("MonitorVersion") ?? "unknown", Metadata("MonitorBuildId") ?? "development", 1);
    private static string? Metadata(string key) => typeof(AgentBuild).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(value => value.Key == key)?.Value;
    public bool IsRelease => System.Version.TryParse(Version, out _) && BuildId is { Length: 40 } && BuildId.All(Uri.IsHexDigit);
}
public sealed record AgentUpdateCapability(int Protocol, bool Enabled);
public sealed record AgentRelease(string Version, string BuildId, int Protocol, string PayloadSha256, string Sha256, long Bytes)
{
    public bool Valid => System.Version.TryParse(Version, out var version) && version.Major >= 0
        && BuildId is { Length: 40 } && BuildId.All(Uri.IsHexDigit) && Protocol == 1
        && Hash(PayloadSha256) && Hash(Sha256) && Bytes is > 0 and <= 512L * 1024 * 1024;
    public static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
public sealed record AgentUpdateRequest(string ComputerId, string Password, string ExpectedSessionId, string Sha256);
public sealed record AgentUpdateTicket(string Id, string DeviceId, string SourceSessionId, string SourceBuildId,
    AgentRelease Release, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public sealed record AgentUpdateReport(string Id, string Phase, string? Message = null);
public sealed record AgentUpdatePoll(string SessionId);
public sealed record AgentUpdatePollResponse(AgentUpdateTicket? Ticket);
public sealed record AgentUpdateOperation(AgentUpdateTicket Ticket, string Phase, string? Message, DateTimeOffset UpdatedAt);
public sealed record AgentUpdateStatus(string ComputerId, AgentBuild? Current, string SessionId, AgentRelease? Release,
    bool CanUpdate, string? Reason, AgentUpdateOperation? Operation);
public sealed record AgentUpdateHealth(bool Ready);
public static class AgentUpdatePhases
{
    public static bool Finished(string phase) => phase is "succeeded" or "rolledBack" or "failed" or "repaired";
}
