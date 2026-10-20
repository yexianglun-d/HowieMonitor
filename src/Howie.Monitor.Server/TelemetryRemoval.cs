using Howie.Monitor.Contracts;

namespace Howie.Monitor.Server;

public sealed partial class TelemetryStore
{
    // 通知 JSON 与 SQLite 不能共用事务；保留未完成标记，重启后继续清理而不恢复接入权限。
    public IReadOnlyList<string> PendingComputerRemovals()
    {
        lock (_gate)
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT device FROM computer_removals";
            using var rows = command.ExecuteReader(); List<string> ids = [];
            while (rows.Read()) ids.Add(rows.GetString(0)); return ids;
        }
    }
    public bool WithComputer(string id, Action<DeviceRegistration> action)
    {
        lock (_gate)
        {
            using var db = Open();
            if (ScalarLong(db, "SELECT 1 FROM computer_removals WHERE device=$id", ("$id", id)) is not null || Registration(db, id) is not { } device) return false;
            action(device); return true;
        }
    }
    public void BeginComputerRemoval(string id)
    {
        lock (_gate)
        {
            using var db = Open(); var pending = Pending(db); using var transaction = db.BeginTransaction();
            Execute(db, transaction, "INSERT OR IGNORE INTO computer_removals VALUES($id); DELETE FROM enrollment_devices WHERE device=$id;", ("$id", id));
            if (pending is not null && (pending.DeviceId == id || pending.ExpectedDeviceId == id))
                Execute(db, transaction, "DELETE FROM enrollment_pending");
            // 保留空的引导行，避免启动时重新导入已撤销的旧引导凭据。
            Execute(db, transaction, """
                UPDATE enrollment_active SET device=COALESCE((SELECT device FROM enrollment_devices ORDER BY device LIMIT 1),''),
                    hash=COALESCE((SELECT hash FROM enrollment_devices ORDER BY device LIMIT 1),'') WHERE device=$id;
                """, ("$id", id));
            transaction.Commit();
        }
    }
    public void CompleteComputerRemoval(string id)
    {
        lock (_gate)
        {
            using var db = Open(); using var transaction = db.BeginTransaction();
            foreach (string table in new[] { "samples", "computer_latest", "computer_heartbeat", "history_samples", "history_values", "history_metric_buckets", "history_computers" })
                Execute(db, transaction, $"DELETE FROM {table} WHERE device=$id", ("$id", id));
            Execute(db, transaction, """
                DELETE FROM latest WHERE json_extract(payload,'$.deviceId')=$id;
                DELETE FROM heartbeat WHERE json_extract(payload,'$.deviceId')=$id;
                DELETE FROM device WHERE id=$id;
                DELETE FROM computer_removals WHERE device=$id;
                """, ("$id", id));
            transaction.Commit();
        }
    }
}
