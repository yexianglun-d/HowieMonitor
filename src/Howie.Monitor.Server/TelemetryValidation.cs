using Howie.Monitor.Contracts;

namespace Howie.Monitor.Server;

public static class TelemetryValidation
{
    public static string? Validate(IngestRequest request, string deviceId, DateTimeOffset now)
    {
        if (request.Samples is null || request.Samples.Count is < 1 or > 30) return "每批允许 1–30 个样本。";
        foreach (var sample in request.Samples)
        {
            if (sample is null || sample.DeviceId != deviceId || !Guid.TryParse(sample.SessionId, out _)
                || sample.BootId?.Length is not (> 0 and <= 128) || sample.Sequence < 0
                || sample.SampledAt < now.AddDays(-1) || sample.SampledAt > now.AddMinutes(5)
                || !double.IsFinite(sample.UptimeSeconds) || sample.UptimeSeconds < 0
                || !double.IsFinite(sample.CollectionMilliseconds) || sample.CollectionMilliseconds < 0
                || sample.Metrics is null || sample.Metrics.Count > 2048
                || sample.Processes is null || sample.Processes.Count > 4096
                || sample.Issues is null || sample.Issues.Count > 256) return "样本身份、时间或内容超出有效范围。";
            foreach (var metric in sample.Metrics)
            {
                if (metric is null || metric.Key?.Length is not (> 0 and <= 200) || metric.DeviceId?.Length is not (> 0 and <= 200)
                    || metric.Name?.Length is not (> 0 and <= 200) || metric.Unit?.Length is not (> 0 and <= 20)
                    || metric.Source?.Length is not (> 0 and <= 512)
                    || metric.Reason?.Length > 512 || !Enum.IsDefined(metric.Status)
                    || (metric.Status == MetricStatus.Available ? !metric.Value.HasValue : metric.Value.HasValue)
                    || (metric.Value is { } value && (!double.IsFinite(value) || (metric.Unit == "%" && value is < 0 or > 100))))
                    return "指标数值、状态或标识不正确。";
            }
            if (sample.Metrics.GroupBy(metric => (metric.DeviceId, metric.Key)).Any(group => group.Count() > 1)) return "样本包含重复指标。";
            foreach (var process in sample.Processes)
                if (process is null || process.Pid < 0 || process.Name?.Length > 256 || process.WorkingSetBytes < 0
                    || process.CpuPercent is { } cpu && (!double.IsFinite(cpu) || cpu is < 0 or > 100)
                    || !Enum.IsDefined(process.Status)) return "进程数据不正确。";
        }
        return null;
    }
}
