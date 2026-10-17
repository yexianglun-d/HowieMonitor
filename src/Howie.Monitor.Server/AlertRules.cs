using Howie.Monitor.Contracts;

namespace Howie.Monitor.Server;

public sealed record AlertRule(string Id, string Name, bool Enabled, string Kind, string Key, string MetricDeviceId,
    string Unit, string Comparison, double Threshold, int DurationSeconds, double RecoveryThreshold,
    int RecoverySeconds, bool NotifyRecovery);
public sealed record NotificationSettings(bool Enabled, int DailyLimit, bool HasCredential, IReadOnlyList<AlertRule> Rules);
public sealed record NotificationUpdate(bool Enabled, int DailyLimit, string? SendKey, bool ClearCredential, IReadOnlyList<AlertRule>? Rules);
public sealed record AlertEvent(string Id, string? RuleId, string Kind, string Title, string Description,
    DateTimeOffset CreatedAt, string Status, string? Detail = null, DateTimeOffset? SentAt = null);
public sealed record NotificationView(NotificationSettings Settings, IReadOnlyList<AlertEvent> Events,
    int RequestsToday, string BudgetDate);
public sealed class AlertRuleState
{
    public bool Triggered { get; set; }
    public DateTimeOffset? Since { get; set; }
    public DateTimeOffset? LastSampledAt { get; set; }
    public string? SessionId { get; set; }
    public long Sequence { get; set; } = -1;
}

public static class AlertRules
{
    public static string? Validate(NotificationUpdate update)
    {
        if (update.DailyLimit is < 1 or > 1000 || update.Rules is null || update.Rules.Count > 20)
            return "每日预算需为 1–1000，规则最多 20 项。";
        if (update.SendKey is not null && !ServerChanSender.ValidKey(update.SendKey))
            return "请填写 Server酱 Turbo 的 SCT 开头 SendKey。";
        if (update.ClearCredential && update.SendKey is not null) return "清除和更新密钥不能同时执行。";
        HashSet<string> ids = [];
        foreach (var rule in update.Rules)
        {
            if (rule is null || !Guid.TryParse(rule.Id, out _) || !ids.Add(rule.Id) || string.IsNullOrWhiteSpace(rule.Name)
                || rule.Name.Length > 64 || rule.Name.Any(char.IsControl) || rule.Kind is not ("metric" or "offline")
                || rule.DurationSeconds is < 2 or > 86400 || rule.RecoverySeconds is < 2 or > 86400)
                return "规则名称、身份、类型或持续时间无效；持续时间支持 2–86400 秒。";
            if (rule.Kind == "metric" && (string.IsNullOrWhiteSpace(rule.Key) || rule.Key.Length > 200
                || string.IsNullOrWhiteSpace(rule.MetricDeviceId) || rule.MetricDeviceId.Length > 200
                || string.IsNullOrWhiteSpace(rule.Unit) || rule.Unit.Length > 20
                || rule.Comparison is not ("above" or "below") || !double.IsFinite(rule.Threshold)
                || !double.IsFinite(rule.RecoveryThreshold)
                || (rule.Comparison == "above" ? rule.RecoveryThreshold > rule.Threshold : rule.RecoveryThreshold < rule.Threshold)))
                return "请选择有效指标、单位和阈值；高值提醒的恢复阈值不得更高，低值提醒的恢复阈值不得更低。";
        }
        return null;
    }

    public static string? Evaluate(AlertRule rule, AlertRuleState state, LatestView view,
        DateTimeOffset now, DateTimeOffset startedAt)
    {
        if (rule.Kind == "offline")
        {
            // 重启后的失联时间重新观察，不能把服务端停机时间当作电脑断线证据。
            var contact = view.LastHeartbeat;
            if (view.ReceivedAt.HasValue && (!contact.HasValue || view.ReceivedAt > contact)) contact = view.ReceivedAt;
            var baseline = contact.HasValue && contact.Value > startedAt ? contact.Value : startedAt;
            bool absent = now - baseline > TimeSpan.FromSeconds(30);
            bool recovered = contact.HasValue && contact.Value >= startedAt && now - contact.Value <= TimeSpan.FromSeconds(30);
            return Advance(rule, state, absent, recovered, now, absent ? baseline.AddSeconds(30) : null);
        }
        var sample = view.Sample;
        if (view.DataStatus != "fresh" || sample is null)
        { state.Since = null; state.LastSampledAt = null; return null; }
        if (state.SessionId == sample.SessionId && state.Sequence >= sample.Sequence) return null;
        var metric = sample.Metrics.FirstOrDefault(value => value.Key == rule.Key && value.DeviceId == rule.MetricDeviceId && value.Unit == rule.Unit);
        bool continuous = state.SessionId == sample.SessionId && state.LastSampledAt.HasValue
            && sample.SampledAt > state.LastSampledAt && sample.SampledAt - state.LastSampledAt <= TimeSpan.FromSeconds(5);
        state.SessionId = sample.SessionId; state.Sequence = sample.Sequence;
        if (metric?.Status != MetricStatus.Available || metric.Value is not double value || !double.IsFinite(value))
        { state.Since = null; state.LastSampledAt = null; return null; }
        if (!continuous) state.Since = null;
        state.LastSampledAt = sample.SampledAt;
        bool high = rule.Comparison == "above";
        return Advance(rule, state, high ? value >= rule.Threshold : value <= rule.Threshold,
            high ? value <= rule.RecoveryThreshold : value >= rule.RecoveryThreshold, sample.SampledAt);
    }

    private static string? Advance(AlertRule rule, AlertRuleState state, bool condition, bool recovery,
        DateTimeOffset at, DateTimeOffset? firstEvidence = null)
    {
        if (!(state.Triggered ? recovery : condition)) { state.Since = null; return null; }
        state.Since ??= firstEvidence ?? at;
        int seconds = state.Triggered ? rule.RecoverySeconds : rule.DurationSeconds;
        if (at - state.Since.Value < TimeSpan.FromSeconds(seconds)) return null;
        bool recovering = state.Triggered;
        state.Triggered = !recovering; state.Since = null;
        return recovering ? "recovery" : "trigger";
    }
}
