namespace Howie.Monitor.Contracts;

public enum MetricStatus
{
    Available,
    NotReady,
    PermissionDenied,
    Unsupported,
    Failed,
    DriverMissing,
    Unknown
}

public sealed record MetricReading(
    string Key, string DeviceId, string Name, double? Value, string Unit,
    string Source, MetricStatus Status, string? Reason)
{
    public static MetricReading Present(string key, string deviceId, string name,
        double value, string unit, string source)
    {
        if (!double.IsFinite(value) || (unit == "%" && (value < 0 || value > 100)))
        {
            return Missing(key, deviceId, name, unit, source, MetricStatus.Failed,
                "来源返回非有限数值或超出 0–100 的百分比。");
        }

        return new(key, deviceId, name, value, unit, source, MetricStatus.Available, null);
    }

    public static MetricReading Missing(string key, string deviceId, string name,
        string unit, string source, MetricStatus status, string reason) =>
        new(key, deviceId, name, null, unit, source, status, reason);
}

public sealed record CollectionIssue(string Source, MetricStatus Status, string Reason);
public sealed record CpuInfo(string Id, string Name, int? PhysicalCores, int? LogicalProcessors);
public sealed record DisplayAdapterInfo(string Id, string Name, string Vendor, bool IsHardwareGpu);
public sealed record DiskInfo(string Id, uint? Index, string Name, ulong? SizeBytes);
public sealed record PartitionInfo(string Id, uint? DiskIndex, ulong? SizeBytes);
public sealed record VolumeInfo(string Id, string FileSystem, ulong? SizeBytes,
    ulong? FreeBytes, IReadOnlyList<string> PartitionIds);
public sealed record NetworkInfo(string Id, string Name, string Type, string Status);
public sealed record DriverInfo(string Name, string State, string StartMode);
public sealed record Inventory(string OperatingSystem, string OsVersion, IReadOnlyList<CpuInfo> Cpus,
    IReadOnlyList<DisplayAdapterInfo> DisplayAdapters, IReadOnlyList<DiskInfo> PhysicalDisks,
    IReadOnlyList<PartitionInfo> Partitions, IReadOnlyList<VolumeInfo> Volumes,
    IReadOnlyList<NetworkInfo> Networks, IReadOnlyList<CollectionIssue> Issues);
public sealed record SensorEnvironment(bool IsAdministrator, bool PawnIoInstalled,
    string? PawnIoVersion, bool PawnIoDeviceAccessible, int? PawnIoOpenError,
    IReadOnlyList<DriverInfo> Drivers, IReadOnlyList<CollectionIssue> Issues);
public sealed record ProbeSample(int Sequence, DateTimeOffset SampledAt, double UptimeSeconds,
    double CollectionMilliseconds, double? CollectorCpuPercent, long CollectorWorkingSetBytes,
    IReadOnlyList<MetricReading> Metrics, IReadOnlyList<CollectionIssue> Issues);
