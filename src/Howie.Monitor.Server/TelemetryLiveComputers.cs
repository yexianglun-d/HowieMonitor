using System.Text.Json;
using System.Text.RegularExpressions;
using Howie.Monitor.Contracts;
using Microsoft.Data.Sqlite;

namespace Howie.Monitor.Server;

public sealed partial class TelemetryStore
{
    private static void InitializeLiveComputers(SqliteConnection db)
    {
        Execute(db, null, """
            CREATE TABLE IF NOT EXISTS enrollment_active(id INTEGER PRIMARY KEY CHECK(id=1),device TEXT NOT NULL,hash TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS computer_latest(device TEXT PRIMARY KEY,session TEXT NOT NULL,
                sequence INTEGER NOT NULL,received INTEGER NOT NULL,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS computer_heartbeat(device TEXT PRIMARY KEY,received INTEGER NOT NULL,payload TEXT NOT NULL);
            """);
        using var marker = db.CreateCommand();
        marker.CommandText = "SELECT value FROM history_metadata WHERE name='multi-computer-v1'";
        if (marker.ExecuteScalar() is not null) return;
        // 改复合主键、复制旧实时状态和落迁移标记必须同一事务，失败时保留原库。
        using var transaction = db.BeginTransaction();
        Execute(db, transaction, """
            CREATE TABLE samples_multi(device TEXT NOT NULL,session TEXT NOT NULL,sequence INTEGER NOT NULL,
                sampled INTEGER NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(device,session,sequence));
            INSERT INTO samples_multi SELECT json_extract(payload,'$.deviceId'),session,sequence,sampled,payload FROM samples;
            DROP TABLE samples;
            ALTER TABLE samples_multi RENAME TO samples;
            CREATE INDEX ix_samples_time ON samples(sampled);
            INSERT INTO computer_latest SELECT json_extract(payload,'$.deviceId'),session,sequence,received,payload FROM latest;
            INSERT INTO computer_heartbeat SELECT json_extract(payload,'$.deviceId'),received,payload FROM heartbeat;
            INSERT INTO history_metadata VALUES('multi-computer-v1','complete');
            """);
        transaction.Commit();
    }

    private static DeviceRegistration? Registration(SqliteConnection db, string? computerId = null, SqliteTransaction? transaction = null)
        => computerId is null
            ? ReadJson<DeviceRegistration>(db, "SELECT registration FROM device ORDER BY id=COALESCE((SELECT device FROM enrollment_active WHERE id=1),'') DESC,rowid LIMIT 1", transaction)
            : ReadJson<DeviceRegistration>(db, "SELECT registration FROM device WHERE id=$device", transaction, ("$device", computerId));

    private static LatestView ReadLatest(SqliteConnection db, string? computerId, DateTimeOffset now)
    {
        var device = Registration(db, computerId);
        string id = device?.DeviceId ?? "";
        var sample = ReadJson<TelemetrySample>(db, "SELECT payload FROM computer_latest WHERE device=$device", null, ("$device", id));
        var beat = ReadJson<Heartbeat>(db, "SELECT payload FROM computer_heartbeat WHERE device=$device", null, ("$device", id));
        long? received = ScalarLong(db, "SELECT received FROM computer_latest WHERE device=$device", ("$device", id));
        long? heartbeatAt = ScalarLong(db, "SELECT received FROM computer_heartbeat WHERE device=$device", ("$device", id));
        bool connected = heartbeatAt.HasValue && now.ToUnixTimeMilliseconds() - heartbeatAt.Value <= 30000
            && beat?.SessionId == device?.SessionId && beat?.BootId == device?.BootId;
        return new(sample, now, received.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(received.Value) : null,
            heartbeatAt.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(heartbeatAt.Value) : null,
            DataStatus(sample, device?.SessionId, now), connected ? "online" : "disconnected", beat);
    }

    private static bool GpuUsageAvailable(MetricReading? metric) => metric is
        { Status: MetricStatus.Available, Unit: "%", Value: >= 0 and <= 100 };

    private static GpuSummary[] GpuSummaries(IReadOnlyList<MetricReading> metrics) => metrics
        .Where(metric => metric.DeviceId.StartsWith("sensor/Gpu", StringComparison.Ordinal) || metric.DeviceId == "gpu")
        .GroupBy(metric => metric.DeviceId).OrderBy(group => group.Key, StringComparer.Ordinal)
        .Select(group =>
        {
            // 显卡各自选核心占用，不能由枚举顺序把空闲核显当成整台电脑的 GPU。
            var usage = group.Where(metric => metric.Unit == "%" && metric.Name is "GPU Core" or "D3D 3D" or "GPU 占用")
                .OrderByDescending(GpuUsageAvailable)
                .ThenBy(metric => metric.Name == "GPU Core" ? 0 : metric.Name == "D3D 3D" ? 1 : 2)
                .ThenBy(metric => metric.Key, StringComparer.Ordinal).FirstOrDefault();
            string source = usage?.Source ?? group.OrderBy(metric => metric.Key, StringComparer.Ordinal).First().Source;
            var name = Regex.Match(source, @"^LibreHardwareMonitorLib[^:]*:\s*(.+)/[^/]+$");
            return new GpuSummary(group.Key, name.Success ? name.Groups[1].Value.Trim() : group.Key, usage);
        }).ToArray();

    public IReadOnlyList<ComputerSummary> Computers(DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand(); command.CommandText = "SELECT registration FROM device ORDER BY rowid";
            List<DeviceRegistration> devices = [];
            using (var rows = command.ExecuteReader())
                while (rows.Read()) devices.Add(JsonSerializer.Deserialize<DeviceRegistration>(rows.GetString(0), ProtocolJson.Options)!);
            return devices.Select(device =>
            {
                var view = ReadLatest(db, device.DeviceId, now);
                double? Metric(string key) => view.DataStatus == "fresh"
                    ? view.Sample?.Metrics.FirstOrDefault(value => value.Key == key && value.Unit == "%" && value.Status == MetricStatus.Available)?.Value : null;
                var metrics = view.DataStatus == "fresh" ? view.Sample?.Metrics ?? [] : [];
                var gpus = GpuSummaries(view.Sample?.Metrics ?? [])
                    .Select(gpu => view.DataStatus == "fresh" ? gpu : gpu with { Usage = null }).ToArray();
                // 兼容旧字段；多显卡没有单一占用值，旧客户端应刷新后使用逐显卡列表。
                double? gpuPercent = gpus.Length == 1 && GpuUsageAvailable(gpus[0].Usage) ? gpus[0].Usage!.Value : null;
                var volume = metrics.Where(value => value.Key == "volume.free" && value.Unit == "B"
                    && value.Status == MetricStatus.Available && value.Value is >= 0).OrderBy(value => value.Value).FirstOrDefault();
                return new ComputerSummary(device.DeviceId, device.Name, view.DataStatus, view.ConnectionStatus,
                    view.Sample?.SampledAt, view.LastHeartbeat, Metric("cpu.usage"), Metric("memory.usage"), gpuPercent,
                    view.DataStatus == "fresh" ? view.Sample?.UptimeSeconds : null,
                    metrics.Where(value => value.Unit == "°C").ToArray(), volume?.DeviceId, volume?.Value, now, gpus);
            }).ToArray();
        }
    }
}
