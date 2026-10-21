using System.Text.Json;
using Microsoft.Data.Sqlite;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Server;

public sealed partial class TelemetryStore
{
    private readonly string _connectionString;
    private readonly object _gate = new();
    public TelemetryStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "monitor.db") }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS device(id TEXT PRIMARY KEY, registration TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS computer_removals(device TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS agent_updates(device TEXT PRIMARY KEY,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS samples(session TEXT NOT NULL, sequence INTEGER NOT NULL,
                sampled INTEGER NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(session,sequence));
            CREATE INDEX IF NOT EXISTS ix_samples_time ON samples(sampled);
            CREATE TABLE IF NOT EXISTS latest(id INTEGER PRIMARY KEY CHECK(id=1), session TEXT NOT NULL,
                sequence INTEGER NOT NULL, received INTEGER NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS heartbeat(id INTEGER PRIMARY KEY CHECK(id=1), received INTEGER NOT NULL, payload TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        InitializeHistory(db);
        InitializeLiveComputers(db);
    }

    public void Register(DeviceRegistration registration)
    {
        lock (_gate)
        {
            using var db = Open();
            using var transaction = db.BeginTransaction();
            Execute(db, transaction, "INSERT INTO device VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET registration=$json",
                ("$id", registration.DeviceId), ("$json", JsonSerializer.Serialize(registration, ProtocolJson.Options)));
            RememberHistoryComputer(db, transaction, registration); transaction.Commit();
        }
    }
    public DeviceRegistration? Device(string? computerId = null)
    {
        lock (_gate) { using var db = Open(); return Registration(db, computerId); }
    }
    public void Beat(Heartbeat heartbeat, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open();
            Execute(db, null, "INSERT INTO computer_heartbeat VALUES($device,$now,$json) ON CONFLICT(device) DO UPDATE SET received=$now,payload=$json",
                ("$device", heartbeat.DeviceId), ("$now", now.ToUnixTimeMilliseconds()), ("$json", JsonSerializer.Serialize(heartbeat, ProtocolJson.Options)));
        }
    }

    public IngestAcknowledgement Ingest(IngestRequest request, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open();
            using var transaction = db.BeginTransaction();
            var acknowledgement = IngestCore(db, transaction, request, now);
            transaction.Commit();
            return acknowledgement;
        }
    }

    private static IngestAcknowledgement IngestCore(SqliteConnection db, SqliteTransaction transaction,
        IngestRequest request, DateTimeOffset now)
    {
            using var historyWriter = new HistoryWriter(db, transaction, now);
            List<SampleIdentity> accepted = [];
            foreach (var sample in request.Samples)
            {
                var registration = Registration(db, sample.DeviceId, transaction);
                // 历史只保存主要进程；完整进程列表保留在最新快照。
                var top = TopProcesses(sample.Processes);
                bool inserted = historyWriter.Add(sample, top, sample.SampledAt <= now.AddSeconds(5));
                if (inserted && sample.SampledAt >= now.AddHours(-1))
                    Execute(db, transaction, "INSERT OR IGNORE INTO samples VALUES($device,$session,$sequence,$time,$json)",
                        ("$device", sample.DeviceId), ("$session", sample.SessionId), ("$sequence", sample.Sequence), ("$time", sample.SampledAt.ToUnixTimeMilliseconds()),
                        ("$json", JsonSerializer.Serialize(sample with { Processes = top }, ProtocolJson.Options)));
                // 最新观测按当前会话和递增序号选择；新鲜度单独判断，首次时钟异常也必须可见。
                if (inserted && !request.Backfill && registration?.SessionId == sample.SessionId)
                {
                    Execute(db, transaction, """
                        INSERT INTO computer_latest VALUES($device,$session,$sequence,$received,$json)
                        ON CONFLICT(device) DO UPDATE SET session=$session,sequence=$sequence,received=$received,payload=$json
                        WHERE computer_latest.session<>$session OR computer_latest.sequence<$sequence
                        """, ("$device", sample.DeviceId), ("$session", sample.SessionId), ("$sequence", sample.Sequence),
                        ("$received", now.ToUnixTimeMilliseconds()), ("$json", JsonSerializer.Serialize(sample, ProtocolJson.Options)));
                }
                accepted.Add(new(sample.SessionId, sample.Sequence));
            }
            Execute(db, transaction, "DELETE FROM samples WHERE rowid IN (SELECT rowid FROM samples WHERE sampled<$cutoff LIMIT 300)",
                ("$cutoff", now.AddHours(-1).ToUnixTimeMilliseconds()));
            DeleteExpired(db, transaction, "history_values", "sampled", now.AddHours(-1).ToUnixTimeMilliseconds(), 5000);
            return new(accepted);
    }

    public LatestView Latest(DateTimeOffset now, string? computerId = null)
    {
        lock (_gate)
        {
            using var db = Open();
            return ReadLatest(db, computerId, now);
        }
    }

    public static string DataStatus(TelemetrySample? sample, string? activeSession, DateTimeOffset now)
    {
        if (sample is null) return "waiting";
        double age = (now - sample.SampledAt).TotalSeconds;
        if (age < -5) return "clockError";
        if (sample.SessionId != activeSession || age > 30) return "stale";
        return age > 10 ? "delayed" : "fresh";
    }

    public IReadOnlyList<HistoryPoint> History(string key, string deviceId, DateTimeOffset from, DateTimeOffset now, string? unit = null, string? computerId = null)
    {
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            string? owner = Registration(db, computerId)?.DeviceId;
            command.CommandText = """
                SELECT h.sampled,v.value FROM history_samples h LEFT JOIN history_values v
                    ON h.device=v.device AND h.session=v.session AND h.sequence=v.sequence
                        AND v.metric_device=$metric AND v.key=$key AND v.unit=$unit
                WHERE h.device=$owner AND h.sampled >= $from AND h.sampled <= $now
                    ORDER BY h.sampled,h.session,h.sequence LIMIT 1800
                """;
            command.Parameters.AddWithValue("$owner", owner ?? "");
            command.Parameters.AddWithValue("$metric", deviceId);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$unit", unit ?? LatestHistoryUnit(db, owner ?? "", key, deviceId) ?? "");
            command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            using var reader = command.ExecuteReader();
            List<HistoryPoint> points = [];
            while (reader.Read())
            {
                points.Add(new(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                    reader.IsDBNull(1) ? null : reader.GetDouble(1)));
            }
            return points;
        }
    }

    public long SampleCount() { lock (_gate) { using var db = Open(); return ScalarLong(db, "SELECT COUNT(*) FROM samples") ?? 0; } }
    private SqliteConnection Open() { var db = new SqliteConnection(_connectionString); db.Open(); return db; }
    private static int Execute(SqliteConnection db, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] args)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var arg in args) command.Parameters.AddWithValue(arg.Name, arg.Value);
        return command.ExecuteNonQuery();
    }
    private static T? ReadJson<T>(SqliteConnection db, string sql, SqliteTransaction? transaction = null, params (string Name, object Value)[] args)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var arg in args) command.Parameters.AddWithValue(arg.Name, arg.Value);
        var value = command.ExecuteScalar();
        return value is string json ? JsonSerializer.Deserialize<T>(json, ProtocolJson.Options) : default;
    }
    private static long? ScalarLong(SqliteConnection db, string sql, params (string Name, object Value)[] args)
    {
        using var command = db.CreateCommand(); command.CommandText = sql;
        foreach (var arg in args) command.Parameters.AddWithValue(arg.Name, arg.Value);
        return command.ExecuteScalar() is long value ? value : null;
    }
}
