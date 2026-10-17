using Howie.Monitor.Contracts;
using Microsoft.Data.Sqlite;

namespace Howie.Monitor.Server;

public sealed partial class TelemetryStore
{
    private static void RememberHistoryComputer(SqliteConnection db, SqliteTransaction? transaction, DeviceRegistration registration) =>
        Execute(db, transaction, "INSERT INTO history_computers VALUES($device,$name) ON CONFLICT(device) DO UPDATE SET name=$name",
            ("$device", registration.DeviceId), ("$name", registration.Name));

    public IReadOnlyList<HistoryComputer> HistoryComputers(DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open(); using var command = db.CreateCommand();
            var current = ReadJson<DeviceRegistration>(db, "SELECT registration FROM device LIMIT 1");
            command.CommandText = """
                WITH retained AS (
                    SELECT device,min(CASE WHEN time_valid=1 THEN sampled END) AS first,
                        max(CASE WHEN time_valid=1 THEN sampled END) AS last
                    FROM history_samples WHERE sampled>=$from AND sampled<=$to GROUP BY device
                ), computers AS (
                    SELECT device FROM retained UNION SELECT $current WHERE $current<>''
                )
                SELECT c.device,n.name,c.device=$current,r.first,r.last FROM computers c
                LEFT JOIN history_computers n ON n.device=c.device LEFT JOIN retained r ON r.device=c.device
                ORDER BY c.device=$current DESC,r.last DESC,c.device LIMIT 100
                """;
            command.Parameters.AddWithValue("$current", current?.DeviceId ?? "");
            command.Parameters.AddWithValue("$from", now.AddDays(-7).ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$to", now.AddSeconds(5).ToUnixTimeMilliseconds());
            using var rows = command.ExecuteReader(); List<HistoryComputer> computers = [];
            while (rows.Read()) computers.Add(new(rows.GetString(0), rows.IsDBNull(1) ? null : rows.GetString(1),
                rows.GetBoolean(2), Stamp(rows, 3), Stamp(rows, 4)));
            return computers;
        }
    }

    public string? ResolveHistoryComputer(string? computerId, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open();
            string current = ReadJson<DeviceRegistration>(db, "SELECT registration FROM device LIMIT 1")?.DeviceId ?? "";
            if (computerId is null) return current;
            if (computerId == current && current.Length > 0) return current;
            using var command = db.CreateCommand();
            command.CommandText = "SELECT 1 FROM history_samples WHERE device=$device AND sampled>=$from AND sampled<=$to LIMIT 1";
            command.Parameters.AddWithValue("$device", computerId);
            command.Parameters.AddWithValue("$from", now.AddDays(-7).ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$to", now.AddSeconds(5).ToUnixTimeMilliseconds());
            return command.ExecuteScalar() is null ? null : computerId;
        }
    }
}
