using System.Text.Json;
using System.Text.Json.Serialization;

namespace Howie.Monitor.Contracts;

public sealed record ProcessReading(int Pid, DateTimeOffset? StartedAt, string Name,
    double? CpuPercent, long? WorkingSetBytes, MetricStatus Status, string? Reason);
public sealed record DeviceRegistration(string DeviceId, string Name, Inventory Inventory,
    SensorEnvironment Sensors, string AgentVersion, string BootId, string SessionId,
    AgentBuild? Build = null, AgentUpdateCapability? Updates = null);
public sealed record TelemetrySample(string DeviceId, string BootId, string SessionId, long Sequence,
    DateTimeOffset SampledAt, double UptimeSeconds, double CollectionMilliseconds,
    IReadOnlyList<MetricReading> Metrics, IReadOnlyList<ProcessReading> Processes,
    IReadOnlyList<CollectionIssue> Issues);
public sealed record IngestRequest(bool Backfill, IReadOnlyList<TelemetrySample> Samples);
public sealed record SampleIdentity(string SessionId, long Sequence);
public sealed record IngestAcknowledgement(IReadOnlyList<SampleIdentity> Accepted);
public sealed record Heartbeat(string DeviceId, string BootId, string SessionId, long LastSequence,
    int QueueLength, long DroppedSamples, string? LastError, DateTimeOffset? DroppedFrom = null, DateTimeOffset? DroppedTo = null);
public sealed record LatestView(TelemetrySample? Sample, DateTimeOffset ServerNow,
    DateTimeOffset? ReceivedAt, DateTimeOffset? LastHeartbeat, string DataStatus,
    string ConnectionStatus, Heartbeat? Agent);
public sealed record HistoryPoint(DateTimeOffset SampledAt, double? Value);
public sealed record HistoryMetric(string Key, string DeviceId, string Name, string Unit);
public sealed record HistoryComputer(string DeviceId, string? Name, bool IsCurrent,
    DateTimeOffset? FirstSampleAt, DateTimeOffset? LastSampleAt);
public sealed record GpuSummary(string DeviceId, string Name, MetricReading? Usage);
public sealed record ComputerSummary(string DeviceId, string Name, string DataStatus, string ConnectionStatus,
    DateTimeOffset? SampledAt, DateTimeOffset? LastHeartbeat, double? CpuPercent, double? MemoryPercent,
    double? GpuPercent = null, double? UptimeSeconds = null, IReadOnlyList<MetricReading>? Temperatures = null,
    string? LowestSpaceVolume = null, double? LowestSpaceBytes = null, DateTimeOffset? ServerNow = null,
    IReadOnlyList<GpuSummary>? Gpus = null, string? AgentVersion = null, AgentBuild? Build = null);
public sealed record HistoryBucketPoint(DateTimeOffset SampledAt, double? Value, double? Minimum, double? Maximum,
    long ValidSamples, long TotalSamples, double CoverageSeconds, DateTimeOffset? FirstSampleAt,
    DateTimeOffset? LastSampleAt, DateTimeOffset? PeakAt, SampleIdentity? Peak);
public sealed record HistorySeries(DateTimeOffset From, DateTimeOffset To, int IntervalSeconds, string Unit,
    IReadOnlyList<HistoryBucketPoint> Points, double? Average, double? Minimum, double? Maximum,
    long ValidSamples, double CoverageSeconds, DateTimeOffset? FirstSampleAt, DateTimeOffset? LastSampleAt,
    DateTimeOffset? PeakAt, SampleIdentity? Peak, long ClockErrorSamples);
public sealed record HistoricalProcesses(DateTimeOffset SampledAt, IReadOnlyList<ProcessReading> Processes);

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
