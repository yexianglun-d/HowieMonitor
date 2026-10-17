using System.Numerics;
using System.Text.Json;
using Howie.Monitor.Contracts;
using Microsoft.Data.Sqlite;

namespace Howie.Monitor.Server;

public sealed partial class TelemetryStore
{
    private static ProcessReading[] TopProcesses(IReadOnlyList<ProcessReading> processes) =>
        processes.Where(process => process.CpuPercent.HasValue).OrderByDescending(process => process.CpuPercent).Take(5)
            .Concat(processes.Where(process => process.WorkingSetBytes.HasValue).OrderByDescending(process => process.WorkingSetBytes).Take(5))
            .DistinctBy(process => (process.Pid, process.StartedAt)).ToArray();

    private static long Bucket(long time, int seconds) => time - (time % (seconds * 1000L) + seconds * 1000L) % (seconds * 1000L);

    private static void InitializeHistory(SqliteConnection db)
    {
        Execute(db, null, """
            CREATE TABLE IF NOT EXISTS history_samples(device TEXT NOT NULL,session TEXT NOT NULL,sequence INTEGER NOT NULL,
                sampled INTEGER NOT NULL,processes TEXT NOT NULL,time_valid INTEGER NOT NULL,PRIMARY KEY(device,session,sequence));
            CREATE INDEX IF NOT EXISTS ix_history_samples_time ON history_samples(sampled);
            CREATE INDEX IF NOT EXISTS ix_history_samples_device_time ON history_samples(device,sampled);
            CREATE TABLE IF NOT EXISTS history_values(device TEXT NOT NULL,session TEXT NOT NULL,sequence INTEGER NOT NULL,
                sampled INTEGER NOT NULL,metric_device TEXT NOT NULL,key TEXT NOT NULL,unit TEXT NOT NULL,value REAL,
                PRIMARY KEY(device,session,sequence,metric_device,key,unit));
            CREATE INDEX IF NOT EXISTS ix_history_values_query ON history_values(device,metric_device,key,unit,sampled);
            CREATE INDEX IF NOT EXISTS ix_history_values_time ON history_values(sampled);
            CREATE TABLE IF NOT EXISTS history_metric_buckets(device TEXT NOT NULL,metric_device TEXT NOT NULL,key TEXT NOT NULL,
                unit TEXT NOT NULL,seconds INTEGER NOT NULL,bucket INTEGER NOT NULL,name TEXT NOT NULL,
                mean REAL,minimum REAL,maximum REAL,valid_count INTEGER NOT NULL,total_count INTEGER NOT NULL,
                coverage_a INTEGER NOT NULL,coverage_b INTEGER NOT NULL,coverage_c INTEGER NOT NULL,
                first_valid INTEGER,last_valid INTEGER,peak_time INTEGER,peak_session TEXT,peak_sequence INTEGER,
                PRIMARY KEY(device,metric_device,key,unit,seconds,bucket));
            CREATE INDEX IF NOT EXISTS ix_history_buckets_time ON history_metric_buckets(seconds,bucket);
            CREATE TABLE IF NOT EXISTS history_metadata(name TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS history_computers(device TEXT PRIMARY KEY,name TEXT NOT NULL);
            """);
        var current = ReadJson<DeviceRegistration>(db, "SELECT registration FROM device LIMIT 1");
        if (current is not null) RememberHistoryComputer(db, null, current);
        using var marker = db.CreateCommand();
        marker.CommandText = "SELECT value FROM history_metadata WHERE name='raw-import-v1'";
        if (marker.ExecuteScalar() is not null) return;

        // 分批导入旧原始数据；同一事务写身份与汇总，中途重启也不会重复计数。
        long cursor = 0;
        var now = DateTimeOffset.UtcNow;
        while (true)
        {
            List<(long Id, TelemetrySample Sample)> batch = [];
            using (var read = db.CreateCommand())
            {
                read.CommandText = "SELECT rowid,payload FROM samples WHERE rowid>$cursor ORDER BY rowid LIMIT 100";
                read.Parameters.AddWithValue("$cursor", cursor);
                using var rows = read.ExecuteReader();
                while (rows.Read()) batch.Add((rows.GetInt64(0), JsonSerializer.Deserialize<TelemetrySample>(rows.GetString(1), ProtocolJson.Options)!));
            }
            if (batch.Count == 0) break;
            using var transaction = db.BeginTransaction();
            using var writer = new HistoryWriter(db, transaction, now);
            foreach (var row in batch)
            {
                // 旧库没有每条 receivedAt，保留原时间与实测值，不反推或伪造接收时间。
                writer.Add(row.Sample, TopProcesses(row.Sample.Processes), row.Sample.SampledAt <= now.AddSeconds(5));
                cursor = row.Id;
            }
            transaction.Commit();
        }
        Execute(db, null, "INSERT INTO history_metadata VALUES('raw-import-v1','complete')");
    }

    private sealed class HistoryWriter : IDisposable
    {
        private readonly SqliteCommand _sample, _value, _bucket;
        private readonly DateTimeOffset _now;
        public HistoryWriter(SqliteConnection db, SqliteTransaction transaction, DateTimeOffset now)
        {
            _now = now;
            _sample = Prepare(db, transaction, "INSERT OR IGNORE INTO history_samples VALUES($device,$session,$sequence,$time,$processes,$timeValid)",
                "device", "session", "sequence", "time", "processes", "timeValid");
            _value = Prepare(db, transaction, "INSERT INTO history_values VALUES($device,$session,$sequence,$time,$metric,$key,$unit,$value)",
                "device", "session", "sequence", "time", "metric", "key", "unit", "value");
            _bucket = Prepare(db, transaction, """
                INSERT INTO history_metric_buckets VALUES($device,$metric,$key,$unit,$seconds,$bucket,$name,
                    $value,$value,$value,$valid,1,$a,$b,$c,$validTime,$validTime,$validTime,$peakSession,$peakSequence)
                ON CONFLICT(device,metric_device,key,unit,seconds,bucket) DO UPDATE SET
                    name=excluded.name,
                    mean=CASE WHEN excluded.valid_count=0 THEN mean WHEN valid_count=0 THEN excluded.mean
                        ELSE mean*(valid_count*1.0/(valid_count+1))+excluded.mean/(valid_count+1) END,
                    minimum=CASE WHEN excluded.valid_count=0 THEN minimum WHEN valid_count=0 THEN excluded.minimum ELSE min(minimum,excluded.minimum) END,
                    peak_time=CASE WHEN excluded.valid_count>0 AND (valid_count=0 OR excluded.maximum>maximum
                        OR (excluded.maximum=maximum AND excluded.peak_time<peak_time)) THEN excluded.peak_time ELSE peak_time END,
                    peak_session=CASE WHEN excluded.valid_count>0 AND (valid_count=0 OR excluded.maximum>maximum
                        OR (excluded.maximum=maximum AND excluded.peak_time<peak_time)) THEN excluded.peak_session ELSE peak_session END,
                    peak_sequence=CASE WHEN excluded.valid_count>0 AND (valid_count=0 OR excluded.maximum>maximum
                        OR (excluded.maximum=maximum AND excluded.peak_time<peak_time)) THEN excluded.peak_sequence ELSE peak_sequence END,
                    maximum=CASE WHEN excluded.valid_count=0 THEN maximum WHEN valid_count=0 THEN excluded.maximum ELSE max(maximum,excluded.maximum) END,
                    first_valid=CASE WHEN excluded.valid_count=0 THEN first_valid WHEN valid_count=0 THEN excluded.first_valid ELSE min(first_valid,excluded.first_valid) END,
                    last_valid=CASE WHEN excluded.valid_count=0 THEN last_valid WHEN valid_count=0 THEN excluded.last_valid ELSE max(last_valid,excluded.last_valid) END,
                    valid_count=valid_count+excluded.valid_count,total_count=total_count+1,
                    coverage_a=coverage_a|excluded.coverage_a,coverage_b=coverage_b|excluded.coverage_b,coverage_c=coverage_c|excluded.coverage_c
                """, "device", "metric", "key", "unit", "seconds", "bucket", "name", "value", "valid", "a", "b", "c", "validTime", "peakSession", "peakSequence");
        }

        public bool Add(TelemetrySample sample, IReadOnlyList<ProcessReading> processes, bool timeValid)
        {
            long time = sample.SampledAt.ToUnixTimeMilliseconds();
            Set(_sample, ("device", sample.DeviceId), ("session", sample.SessionId), ("sequence", sample.Sequence),
                ("time", time), ("processes", JsonSerializer.Serialize(processes, ProtocolJson.Options)), ("timeValid", timeValid ? 1 : 0));
            if (_sample.ExecuteNonQuery() == 0) return false;
            foreach (var metric in sample.Metrics)
            {
                double? value = timeValid && metric.Status == MetricStatus.Available && metric.Value is { } candidate && double.IsFinite(candidate) ? candidate : null;
                if (sample.SampledAt >= _now.AddHours(-1))
                {
                    Set(_value, ("device", sample.DeviceId), ("session", sample.SessionId), ("sequence", sample.Sequence),
                        ("time", time), ("metric", metric.DeviceId), ("key", metric.Key), ("unit", metric.Unit), ("value", value));
                    _value.ExecuteNonQuery();
                }
                foreach (int seconds in new[] { 60, 300 })
                {
                    long bucket = Bucket(time, seconds);
                    if (bucket < Bucket((seconds == 60 ? _now.AddHours(-24) : _now.AddDays(-7)).ToUnixTimeMilliseconds(), seconds)) continue;
                    int slot = (int)((time - bucket) / 2000);
                    long mask = value.HasValue ? 1L << (slot % 64) : 0;
                    Set(_bucket, ("device", sample.DeviceId), ("metric", metric.DeviceId), ("key", metric.Key), ("unit", metric.Unit),
                        ("seconds", seconds), ("bucket", bucket), ("name", metric.Name), ("value", value), ("valid", value.HasValue ? 1 : 0),
                        ("a", slot < 64 ? mask : 0), ("b", slot is >= 64 and < 128 ? mask : 0), ("c", slot >= 128 ? mask : 0),
                        ("validTime", value.HasValue ? time : null), ("peakSession", value.HasValue ? sample.SessionId : null),
                        ("peakSequence", value.HasValue ? sample.Sequence : null));
                    _bucket.ExecuteNonQuery();
                }
            }
            return true;
        }

        public void Dispose() { _sample.Dispose(); _value.Dispose(); _bucket.Dispose(); }
        private static SqliteCommand Prepare(SqliteConnection db, SqliteTransaction transaction, string sql, params string[] names)
        {
            var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
            foreach (string name in names) command.Parameters.AddWithValue("$" + name, DBNull.Value);
            command.Prepare(); return command;
        }
        private static void Set(SqliteCommand command, params (string Name, object? Value)[] values)
        { foreach (var value in values) command.Parameters["$" + value.Name].Value = value.Value ?? DBNull.Value; }
    }

    private static string? LatestHistoryUnit(SqliteConnection db, string owner, string key, string metric)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT unit FROM history_metric_buckets WHERE device=$device AND metric_device=$metric AND key=$key ORDER BY bucket DESC LIMIT 1";
        command.Parameters.AddWithValue("$device", owner); command.Parameters.AddWithValue("$metric", metric); command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public IReadOnlyList<HistoryMetric> HistoryMetrics(string device)
    {
        lock (_gate)
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT key,metric_device,max(name),unit FROM history_metric_buckets WHERE device=$device GROUP BY key,metric_device,unit ORDER BY metric_device,key,unit";
            command.Parameters.AddWithValue("$device", device); using var reader = command.ExecuteReader();
            List<HistoryMetric> metrics = [];
            while (reader.Read()) metrics.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            return metrics;
        }
    }

    public HistorySeries QueryHistory(string device, string key, string metric, string unit, int hours, DateTimeOffset now)
    {
        if (hours is not (1 or 24 or 168)) throw new ArgumentOutOfRangeException(nameof(hours));
        lock (_gate)
        {
            using var db = Open();
            int seconds = hours == 1 ? 2 : hours == 24 ? 60 : 300;
            long from = Bucket(now.AddHours(-hours).ToUnixTimeMilliseconds(), seconds), to = now.ToUnixTimeMilliseconds();
            var values = seconds == 2 ? ReadRawBuckets(db, device, key, metric, unit, from, to) : ReadAggregateBuckets(db, device, key, metric, unit, seconds, from, to);
            List<HistoryBucketPoint> points = [];
            for (long time = from; time <= to; time += seconds * 1000L)
                points.Add(values.GetValueOrDefault(time) ?? new(DateTimeOffset.FromUnixTimeMilliseconds(time), null, null, null, 0, 0, 0, null, null, null, null));
            var valid = points.Where(point => point.ValidSamples > 0).ToArray();
            long count = 0; double? mean = null;
            foreach (var point in valid)
            {
                mean = count == 0 ? point.Value : mean * (count * 1d / (count + point.ValidSamples)) + point.Value * (point.ValidSamples * 1d / (count + point.ValidSamples));
                count += point.ValidSamples;
            }
            var peak = valid.OrderByDescending(point => point.Maximum).ThenBy(point => point.PeakAt).FirstOrDefault();
            using var errors = db.CreateCommand();
            errors.CommandText = "SELECT count(*) FROM history_samples WHERE device=$device AND sampled BETWEEN $from AND $to AND time_valid=0";
            errors.Parameters.AddWithValue("$device", device); errors.Parameters.AddWithValue("$from", from); errors.Parameters.AddWithValue("$to", to);
            return new(DateTimeOffset.FromUnixTimeMilliseconds(from), now, seconds, unit, points, mean,
                valid.Length == 0 ? null : valid.Min(point => point.Minimum), peak?.Maximum, count, valid.Sum(point => point.CoverageSeconds),
                valid.Length == 0 ? null : valid.Min(point => point.FirstSampleAt), valid.Length == 0 ? null : valid.Max(point => point.LastSampleAt),
                peak?.PeakAt, peak?.Peak, Convert.ToInt64(errors.ExecuteScalar()));
        }
    }

    private static Dictionary<long, HistoryBucketPoint> ReadAggregateBuckets(SqliteConnection db, string device, string key, string metric, string unit, int seconds, long from, long to)
    {
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT bucket,mean,minimum,maximum,valid_count,total_count,coverage_a,coverage_b,coverage_c,
                first_valid,last_valid,peak_time,peak_session,peak_sequence
            FROM history_metric_buckets WHERE device=$device AND metric_device=$metric AND key=$key AND unit=$unit
                AND seconds=$seconds AND bucket BETWEEN $from AND $to ORDER BY bucket
            """;
        command.Parameters.AddWithValue("$device", device); command.Parameters.AddWithValue("$metric", metric); command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$unit", unit); command.Parameters.AddWithValue("$seconds", seconds);
        command.Parameters.AddWithValue("$from", from); command.Parameters.AddWithValue("$to", to);
        using var reader = command.ExecuteReader(); var points = new Dictionary<long, HistoryBucketPoint>();
        while (reader.Read())
        {
            long time = reader.GetInt64(0);
            double coverage = 2d * (BitOperations.PopCount((ulong)reader.GetInt64(6)) + BitOperations.PopCount((ulong)reader.GetInt64(7)) + BitOperations.PopCount((ulong)reader.GetInt64(8)));
            points.Add(time, new(DateTimeOffset.FromUnixTimeMilliseconds(time), Number(reader, 1), Number(reader, 2), Number(reader, 3),
                reader.GetInt64(4), reader.GetInt64(5), coverage, Stamp(reader, 9), Stamp(reader, 10), Stamp(reader, 11),
                reader.IsDBNull(12) ? null : new(reader.GetString(12), reader.GetInt64(13))));
        }
        return points;
    }

    private static Dictionary<long, HistoryBucketPoint> ReadRawBuckets(SqliteConnection db, string device, string key, string metric, string unit, long from, long to)
    {
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT h.sampled,v.value,h.session,h.sequence FROM history_samples h LEFT JOIN history_values v
                ON h.device=v.device AND h.session=v.session AND h.sequence=v.sequence
                    AND v.metric_device=$metric AND v.key=$key AND v.unit=$unit
            WHERE h.device=$device AND h.sampled BETWEEN $from AND $to ORDER BY h.sampled,h.session,h.sequence
            """;
        command.Parameters.AddWithValue("$device", device); command.Parameters.AddWithValue("$metric", metric); command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$unit", unit); command.Parameters.AddWithValue("$from", from); command.Parameters.AddWithValue("$to", to);
        using var reader = command.ExecuteReader(); var points = new Dictionary<long, HistoryBucketPoint>();
        while (reader.Read())
        {
            long time = reader.GetInt64(0), bucket = Bucket(time, 2); double? value = Number(reader, 1);
            points.TryGetValue(bucket, out var previous);
            long valid = previous?.ValidSamples ?? 0;
            bool higher = value.HasValue && (previous?.Maximum is not { } maximum || value.Value > maximum);
            var stamp = DateTimeOffset.FromUnixTimeMilliseconds(time);
            double? average = !value.HasValue ? previous?.Value : valid == 0 ? value : previous!.Value * (valid * 1d / (valid + 1)) + value / (valid + 1);
            points[bucket] = new(DateTimeOffset.FromUnixTimeMilliseconds(bucket), average,
                !value.HasValue ? previous?.Minimum : valid == 0 ? value : Math.Min(previous!.Minimum!.Value, value.Value),
                higher ? value : previous?.Maximum, valid + (value.HasValue ? 1 : 0), (previous?.TotalSamples ?? 0) + 1,
                value.HasValue || valid > 0 ? 2 : 0, previous?.FirstSampleAt ?? (value.HasValue ? stamp : null),
                value.HasValue ? stamp : previous?.LastSampleAt, higher ? stamp : previous?.PeakAt,
                higher ? new(reader.GetString(2), reader.GetInt64(3)) : previous?.Peak);
        }
        return points;
    }

    public HistoricalProcesses? HistoryProcesses(string device, SampleIdentity identity)
    {
        lock (_gate)
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT sampled,processes FROM history_samples WHERE device=$device AND session=$session AND sequence=$sequence";
            command.Parameters.AddWithValue("$device", device); command.Parameters.AddWithValue("$session", identity.SessionId); command.Parameters.AddWithValue("$sequence", identity.Sequence);
            using var reader = command.ExecuteReader();
            return reader.Read() ? new(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                JsonSerializer.Deserialize<ProcessReading[]>(reader.GetString(1), ProtocolJson.Options)!) : null;
        }
    }

    public void PruneHistory(DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open(); using var transaction = db.BeginTransaction();
            DeleteExpired(db, transaction, "samples", "sampled", now.AddHours(-1).ToUnixTimeMilliseconds(), 10000);
            DeleteExpired(db, transaction, "history_values", "sampled", now.AddHours(-1).ToUnixTimeMilliseconds(), 10000);
            DeleteExpired(db, transaction, "history_samples", "sampled", Bucket(now.AddDays(-7).ToUnixTimeMilliseconds(), 300), 10000);
            foreach (int seconds in new[] { 60, 300 })
                Execute(db, transaction, "DELETE FROM history_metric_buckets WHERE rowid IN (SELECT rowid FROM history_metric_buckets WHERE seconds=$seconds AND bucket<$cutoff LIMIT 10000)",
                    ("$seconds", seconds), ("$cutoff", Bucket((seconds == 60 ? now.AddHours(-24) : now.AddDays(-7)).ToUnixTimeMilliseconds(), seconds)));
            Execute(db, transaction, "DELETE FROM history_computers WHERE device NOT IN (SELECT id FROM device) AND NOT EXISTS (SELECT 1 FROM history_samples WHERE history_samples.device=history_computers.device)");
            transaction.Commit();
        }
    }

    private static void DeleteExpired(SqliteConnection db, SqliteTransaction transaction, string table, string column, long cutoff, int limit) =>
        Execute(db, transaction, $"DELETE FROM {table} WHERE rowid IN (SELECT rowid FROM {table} WHERE {column}<$cutoff LIMIT $limit)", ("$cutoff", cutoff), ("$limit", limit));
    private static double? Number(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetDouble(index);
    private static DateTimeOffset? Stamp(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(index));
}
