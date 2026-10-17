using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Server;

public sealed class NotificationData
{
    public bool Enabled { get; set; }
    public int DailyLimit { get; set; } = 5;
    public string? ProtectedKey { get; set; }
    public List<AlertRule> Rules { get; set; } = [];
    public Dictionary<string, AlertRuleState> States { get; set; } = [];
    public List<AlertEvent> Events { get; set; } = [];
    public string BudgetDate { get; set; } = "";
    public int RequestsToday { get; set; }
    public DateTimeOffset? LastAttempt { get; set; }
    public string? DeviceId { get; set; }
}
public sealed record NotificationDispatch(AlertEvent Event, string SendKey);

public sealed class NotificationStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly IDataProtector _protector;
    private NotificationData _data;
    public NotificationStore(string directory, IDataProtectionProvider protection)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "notifications.json");
        _protector = protection.CreateProtector("Howie.Monitor.ServerChan.SendKey.v1");
        _data = File.Exists(_path) ? JsonSerializer.Deserialize<NotificationData>(File.ReadAllText(_path), ProtocolJson.Options)! : new();
        // 已交付给外部服务的请求可能已成功；崩溃恢复不能自动重复发送。
        for (int i = 0; i < _data.Events.Count; i++)
            if (_data.Events[i].Status == "sending") _data.Events[i] = _data.Events[i] with { Status = "unknown", Detail = "服务在发送期间重启，结果未知，未自动重发。" };
        foreach (var state in _data.States.Values) { state.Since = null; state.LastSampledAt = null; }
        Save();
    }

    public NotificationView View(DateTimeOffset now)
    {
        lock (_gate)
        {
            string day = Day(now);
            return new(new(_data.Enabled, _data.DailyLimit, _data.ProtectedKey is not null, _data.Rules.ToArray()),
                _data.Events.OrderByDescending(value => value.CreatedAt).ToArray(), day == _data.BudgetDate ? _data.RequestsToday : 0, day);
        }
    }

    public string? Update(NotificationUpdate update)
    {
        string? error = AlertRules.Validate(update);
        if (error is not null) return error;
        lock (_gate)
        {
            if (update.Enabled && (update.ClearCredential || (_data.ProtectedKey is null && update.SendKey is null)))
                return "请先配置 SendKey，再开启通知。";
            string? key = update.ClearCredential ? null : update.SendKey is null ? _data.ProtectedKey : _protector.Protect(update.SendKey);
            var previous = _data;
            _data = JsonSerializer.Deserialize<NotificationData>(JsonSerializer.Serialize(_data, ProtocolJson.Options), ProtocolJson.Options)!;
            _data.Enabled = update.Enabled; _data.DailyLimit = update.DailyLimit; _data.ProtectedKey = key;
            _data.Rules = update.Rules!.ToList(); _data.States.Clear();
            CancelQueued("设置已更新，待发送任务取消。");
            try { Save(); }
            catch (IOException) { _data = previous; throw; }
            return null;
        }
    }

    public string? QueueTest(DateTimeOffset now, string publicUrl)
    {
        lock (_gate)
        {
            if (_data.ProtectedKey is null) return "请先保存 SendKey。";
            ResetBudget(now);
            if (_data.RequestsToday >= _data.DailyLimit) return "今日请求预算已用完，请明日再测试或按实际账号额度调整预算。";
            if (_data.Events.Any(value => value.Kind == "test" && value.Status is "queued" or "sending")) return "已有测试通知等待发送，请刷新发送记录。";
            AddEvent(new(Guid.NewGuid().ToString(), null, "test", "Howie Monitor · 微信通知测试",
                $"这是你在监控设置页主动发起的测试。\n\n时间：{now.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm:ss zzz}\n\n[打开私人监控面板]({publicUrl})", now, "queued"));
            Save(); return null;
        }
    }

    public void Evaluate(DeviceRegistration? device, LatestView view, DateTimeOffset now, DateTimeOffset startedAt, string publicUrl)
    {
        lock (_gate)
        {
            if (device is null || !_data.Enabled) return;
            bool changed = false;
            if (_data.DeviceId != device.DeviceId) { _data.DeviceId = device.DeviceId; _data.States.Clear(); CancelQueued("监控电脑已变更。"); changed = true; }
            foreach (var rule in _data.Rules.Where(value => value.Enabled))
            {
                if (!_data.States.TryGetValue(rule.Id, out var state)) _data.States[rule.Id] = state = new();
                string? transition = AlertRules.Evaluate(rule, state, view, now, startedAt);
                if (transition is not null) changed = true;
                if (transition is null || (transition == "recovery" && !rule.NotifyRecovery)) continue;
                string condition = rule.Kind == "offline" ? "停止收到电脑上报；无法据此判断关机"
                    : $"{rule.Name} { (rule.Comparison == "above" ? "≥" : "≤") } {rule.Threshold} {rule.Unit}，持续 {rule.DurationSeconds} 秒";
                string title = transition == "recovery" ? "监控提醒恢复" : "监控条件触发";
                AddEvent(new(Guid.NewGuid().ToString(), rule.Id, transition, $"Howie Monitor · {title}",
                    $"电脑：{device.Name}\n\n项目：{rule.Name}\n\n状态：{(transition == "recovery" ? "连续有效数据已满足恢复条件" : condition)}\n\n时间：{now.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm:ss zzz}\n\n[打开私人监控面板]({publicUrl})", now, "queued"));
            }
            int count = _data.Events.Count;
            Prune(now);
            // 持续观察的时间与序号只保留在内存；重启本就重新观察，避免每两秒重写整份事件文件。
            if (changed || count != _data.Events.Count) Save();
        }
    }

    public NotificationDispatch? BeginSend(DateTimeOffset now)
    {
        lock (_gate)
        {
            int count = _data.Events.Count;
            Prune(now);
            if (!_data.Events.Any(value => value.Status == "queued"))
            { if (count != _data.Events.Count) Save(); return null; }
            ResetBudget(now);
            for (int i = 0; i < _data.Events.Count; i++)
            {
                var item = _data.Events[i];
                if (item.Status != "queued") continue;
                if (_data.ProtectedKey is null || (item.Kind != "test" && (!_data.Enabled || !_data.Rules.Any(value => value.Id == item.RuleId && value.Enabled))))
                { _data.Events[i] = item with { Status = "cancelled", Detail = "通知或对应规则已关闭。" }; continue; }
                if (now - item.CreatedAt > TimeSpan.FromMinutes(10))
                { _data.Events[i] = item with { Status = "expired", Detail = "等待发送超过 10 分钟，未发送过时提醒。" }; continue; }
                if (_data.RequestsToday >= _data.DailyLimit)
                { _data.Events[i] = item with { Status = "limited", Detail = "今日请求预算已用完，未发送。" }; continue; }
                if (_data.LastAttempt.HasValue && now - _data.LastAttempt < TimeSpan.FromSeconds(60)) { Save(); return null; }
                string key;
                try { key = _protector.Unprotect(_data.ProtectedKey); }
                catch (System.Security.Cryptography.CryptographicException)
                { _data.Events[i] = item with { Status = "failed", Detail = "密钥解密失败，请重新保存 SendKey。" }; continue; }
                var previousAttempt = _data.LastAttempt;
                _data.RequestsToday++; _data.LastAttempt = now;
                var queued = item;
                item = item with { Status = "sending", Detail = null }; _data.Events[i] = item;
                try { Save(); }
                catch (IOException) { _data.Events[i] = queued; _data.RequestsToday--; _data.LastAttempt = previousAttempt; throw; }
                return new(item, key);
            }
            Save(); return null;
        }
    }

    public void Complete(string id, SendResult result, DateTimeOffset now)
    {
        lock (_gate)
        {
            int index = _data.Events.FindIndex(value => value.Id == id && value.Status == "sending");
            if (index < 0) return;
            _data.Events[index] = _data.Events[index] with { Status = result.Status, Detail = result.Detail, SentAt = now }; Save();
        }
    }
    private void CancelQueued(string reason)
    {
        for (int i = 0; i < _data.Events.Count; i++)
            if (_data.Events[i].Status == "queued") _data.Events[i] = _data.Events[i] with { Status = "cancelled", Detail = reason };
    }
    private void AddEvent(AlertEvent item) { _data.Events.Add(item); if (_data.Events.Count > 100) _data.Events.RemoveAt(0); }
    private void Prune(DateTimeOffset now) => _data.Events.RemoveAll(value => value.CreatedAt < now.AddDays(-7) && value.Status != "sending");
    private static string Day(DateTimeOffset now) => now.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    private void ResetBudget(DateTimeOffset now) { string day = Day(now); if (_data.BudgetDate != day) { _data.BudgetDate = day; _data.RequestsToday = 0; } }
    private void Save()
    {
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_data, ProtocolJson.Options));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, _path, true);
    }
}
