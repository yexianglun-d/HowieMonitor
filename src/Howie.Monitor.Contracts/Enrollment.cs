namespace Howie.Monitor.Contracts;

public sealed record EnrollmentCodeRequest(bool ConfirmReplacement, string? ExpectedDeviceId);
public sealed record EnrollmentCode(string Code, DateTimeOffset ExpiresAt, string? PreviousComputerName);
public sealed record EnrollmentClaim(string Code, string RequestId, string ComputerName,
    string? ExistingDeviceId = null, string? ExistingToken = null);
public sealed record EnrollmentCredential(string DeviceId, string Token, DateTimeOffset ExpiresAt,
    string? PreviousComputerName, bool PreservesHistory);
public sealed record EnrollmentProgress(string State, string? ComputerName, bool Registered,
    bool HeartbeatReceived, string DataStatus, DateTimeOffset? SampledAt, string? Detail);
public sealed record EnrollmentOverview(string? CurrentDeviceId, string? CurrentComputerName,
    EnrollmentProgress? Pending, DateTimeOffset? CodeExpiresAt);
public sealed record EnrollmentActivation(string RequestId);
