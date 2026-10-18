using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Howie.Monitor.Contracts;
using Howie.Monitor.Collector;
using LibreHardwareMonitor.Hardware;
using System.Globalization;

namespace Howie.Monitor.Agent;

public sealed record QueueState(int Count, long Dropped, DateTimeOffset? DroppedFrom, DateTimeOffset? DroppedTo);

public sealed class AgentQueue
{
    private readonly string _connectionString;
    private readonly object _gate = new();
    public AgentQueue(string directory)
    {
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "queue.db") }.ToString();
        using var db = Open();
        Run(db, null, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS queue(session TEXT NOT NULL, sequence INTEGER NOT NULL,
                sampled INTEGER NOT NULL, payload TEXT NOT NULL, bytes INTEGER NOT NULL, PRIMARY KEY(session,sequence));
            CREATE INDEX IF NOT EXISTS ix_queue_sampled ON queue(sampled);
            CREATE TABLE IF NOT EXISTS losses(id INTEGER PRIMARY KEY CHECK(id=1), count INTEGER NOT NULL, first INTEGER, last INTEGER);
            INSERT OR IGNORE INTO losses VALUES(1,0,NULL,NULL);
            """);
        UpgradeSensorKeys(db);
    }

    private static void UpgradeSensorKeys(SqliteConnection db)
    {
        if (Number(db, null, "PRAGMA user_version") != 0) return;
        using var transaction = db.BeginTransaction();
        long after = 0;
        while (true)
        {
            long row;
            string json;
            using (var command = db.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT rowid,payload FROM queue WHERE rowid>$after ORDER BY rowid LIMIT 1";
                command.Parameters.AddWithValue("$after", after);
                using var reader = command.ExecuteReader();
                if (!reader.Read()) break;
                row = reader.GetInt64(0); json = reader.GetString(1);
            }
            var sample = JsonSerializer.Deserialize<TelemetrySample>(json, ProtocolJson.Options)
                ?? throw new InvalidOperationException("本地待上报采样无法读取。");
            var metrics = sample.Metrics.Select(metric =>
            {
                string prefix = metric.DeviceId + "/";
                if (!metric.DeviceId.StartsWith("sensor/", StringComparison.Ordinal)
                    || !metric.Key.StartsWith(prefix, StringComparison.Ordinal)
                    || !metric.Source.StartsWith("LibreHardwareMonitorLib 0.9.6:", StringComparison.Ordinal)) return metric;
                var parts = metric.Key[prefix.Length..].Split('/');
                if (parts.Length != 2 || !Enum.TryParse<SensorType>(parts[0], out var type)
                    || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int index)) return metric;
                return metric with { Key = SensorCollector.MetricKey(metric.DeviceId, type, index, metric.Name) };
            }).ToArray();
            if (sample.Metrics.Where((metric, index) => metric.Key != metrics[index].Key).Any())
            {
                // 已积压的旧采样也按相同规则升级，只改变指标标识，不丢弃实际数值或采样身份。
                string upgraded = JsonSerializer.Serialize(sample with { Metrics = metrics }, ProtocolJson.Options);
                Run(db, transaction, "UPDATE queue SET payload=$json,bytes=$bytes WHERE rowid=$row",
                    ("$json", upgraded), ("$bytes", Encoding.UTF8.GetByteCount(upgraded)), ("$row", row));
            }
            after = row;
        }
        Run(db, transaction, "PRAGMA user_version=1"); transaction.Commit();
    }

    public void Add(TelemetrySample sample)
    {
        string json = JsonSerializer.Serialize(sample, ProtocolJson.Options);
        long bytes = Encoding.UTF8.GetByteCount(json);
        lock (_gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            Run(db, tx, "INSERT INTO queue VALUES($session,$sequence,$sampled,$json,$bytes)",
                ("$session", sample.SessionId), ("$sequence", sample.Sequence), ("$sampled", sample.SampledAt.ToUnixTimeMilliseconds()),
                ("$json", json), ("$bytes", bytes));
            // 有界缓存保留近期采样；超大单样本必须显式记录为丢失，不能反复卡住上传。
            while (Number(db, tx, "SELECT COUNT(*) FROM queue") > 1800
                || Number(db, tx, "SELECT COALESCE(SUM(bytes),0) FROM queue") > 64L * 1024 * 1024
                || Number(db, tx, "SELECT COUNT(*) FROM queue WHERE sampled<$cutoff OR bytes>900000",
                    ("$cutoff", DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds())) > 0)
            {
                long? lostAt = NullableNumber(db, tx, "SELECT sampled FROM queue ORDER BY CASE WHEN bytes>900000 THEN 0 ELSE 1 END,sampled LIMIT 1");
                if (!lostAt.HasValue) break;
                Run(db, tx, "UPDATE losses SET count=count+1,first=COALESCE(first,$at),last=$at WHERE id=1", ("$at", lostAt.Value));
                Run(db, tx, "DELETE FROM queue WHERE rowid IN (SELECT rowid FROM queue ORDER BY CASE WHEN bytes>900000 THEN 0 ELSE 1 END,sampled LIMIT 1)");
            }
            tx.Commit();
        }
    }

    public TelemetrySample? Latest()
    {
        lock (_gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT payload FROM queue ORDER BY sampled DESC,sequence DESC LIMIT 1";
            return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<TelemetrySample>(json, ProtocolJson.Options) : null;
        }
    }

    public IReadOnlyList<TelemetrySample> Backlog(DateTimeOffset before)
    {
        lock (_gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT payload,bytes FROM queue WHERE sampled<$before ORDER BY sampled LIMIT 30";
            cmd.Parameters.AddWithValue("$before", before.ToUnixTimeMilliseconds());
            using var reader = cmd.ExecuteReader(); List<TelemetrySample> samples = []; long bytes = 0;
            while (reader.Read())
            {
                long size = reader.GetInt64(1);
                if (bytes + size > 900000) break;
                bytes += size;
                samples.Add(JsonSerializer.Deserialize<TelemetrySample>(reader.GetString(0), ProtocolJson.Options)!);
            }
            return samples;
        }
    }

    public void Acknowledge(IngestAcknowledgement acknowledgement)
    {
        lock (_gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            foreach (var id in acknowledgement.Accepted)
                Run(db, tx, "DELETE FROM queue WHERE session=$session AND sequence=$sequence", ("$session", id.SessionId), ("$sequence", id.Sequence));
            tx.Commit();
        }
    }

    public QueueState State()
    {
        lock (_gate)
        {
            using var db = Open();
            long? from = NullableNumber(db, null, "SELECT first FROM losses WHERE id=1");
            long? to = NullableNumber(db, null, "SELECT last FROM losses WHERE id=1");
            return new((int)Number(db, null, "SELECT COUNT(*) FROM queue"), Number(db, null, "SELECT count FROM losses WHERE id=1"),
                from.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(from.Value) : null,
                to.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(to.Value) : null);
        }
    }
    private SqliteConnection Open() { var db = new SqliteConnection(_connectionString); db.Open(); return db; }
    private static int Run(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Key, object Value)[] args)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var arg in args) cmd.Parameters.AddWithValue(arg.Key, arg.Value);
        return cmd.ExecuteNonQuery();
    }
    private static long Number(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Key, object Value)[] args) => NullableNumber(db, tx, sql, args) ?? 0;
    private static long? NullableNumber(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Key, object Value)[] args)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var arg in args) cmd.Parameters.AddWithValue(arg.Key, arg.Value);
        return cmd.ExecuteScalar() is long number ? number : null;
    }
}
