using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Probe;

public sealed record ProbeReport(string SchemaVersion, string ProbeVersion, string RuntimeVersion,
    DateTimeOffset StartedAt, DateTimeOffset CompletedAt, ProbeOptions Options, bool MatchesReferenceCpuAndGpu,
    Inventory Inventory, SensorEnvironment SensorsBefore, SensorEnvironment SensorsAfter,
    double StartupMilliseconds, double SamplingSeconds, double? SamplingCpuPercent,
    long PeakWorkingSetBytes, bool Cancelled, IReadOnlyList<ProbeSample> Samples);

public static class ReportWriter
{
    public static async Task Write(ProbeReport report)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        string jsonPath = Path.Combine(report.Options.OutputDirectory, "probe-report.json");
        await using (var stream = new FileStream(jsonPath, FileMode.CreateNew, FileAccess.Write))
        {
            await JsonSerializer.SerializeAsync(stream, report, options);
        }
        string markdownPath = Path.Combine(report.Options.OutputDirectory, "probe-report.md");
        await using var markdown = new FileStream(markdownPath, FileMode.CreateNew, FileAccess.Write);
        await using var writer = new StreamWriter(markdown, new UTF8Encoding(false));
        await writer.WriteAsync(Markdown(report));
    }

    public static string Markdown(ProbeReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("# 只读采集探测报告").AppendLine();
        text.AppendLine($"- 环境标签：{report.Options.Label}；场景标签：{report.Options.Scenario}（由运行者提供）。");
        text.AppendLine($"- 采样时间（UTC）：{report.StartedAt:O} 至 {report.CompletedAt:O}。");
        text.AppendLine($"- 样本：{report.Samples.Count}/{report.Options.Samples}；取消：{report.Cancelled}；配置间隔：{report.Options.IntervalSeconds} 秒。");
        text.AppendLine($"- 系统：{Cell(report.Inventory.OperatingSystem)} {Cell(report.Inventory.OsVersion)}。");
        text.AppendLine($"- CPU：{string.Join("；", report.Inventory.Cpus.Select(cpu => Cell(cpu.Name)))}。");
        text.AppendLine($"- 物理 GPU：{string.Join("；", report.Inventory.DisplayAdapters.Where(gpu => gpu.IsHardwareGpu).Select(gpu => Cell(gpu.Name)))}。");
        text.AppendLine($"- CPU/GPU 名称匹配 9950X / 5070 Ti：{report.MatchesReferenceCpuAndGpu}。名称匹配不等同于完成目标机验收。");
        text.AppendLine($"- 管理员权限：{report.SensorsAfter.IsAdministrator}；PawnIO 安装标记：{report.SensorsAfter.PawnIoInstalled}；版本：{report.SensorsAfter.PawnIoVersion ?? "未检测到"}。");
        text.AppendLine($"- PawnIO 设备可访问：{report.SensorsAfter.PawnIoDeviceAccessible}；打开错误码：{report.SensorsAfter.PawnIoOpenError?.ToString() ?? "无"}。");
        text.AppendLine($"- 驱动采样前：{DriverText(report.SensorsBefore)}；采样后：{DriverText(report.SensorsAfter)}。");
        text.AppendLine($"- 程序：{report.ProbeVersion}；运行时：{report.RuntimeVersion}。");
        text.AppendLine().AppendLine("## 短时采样开销").AppendLine();
        text.AppendLine($"- 启动与探测耗时：{Format(report.StartupMilliseconds)} ms（不计入下面的稳态 CPU）。");
        text.AppendLine($"- 采样阶段：{Format(report.SamplingSeconds)} 秒；全机归一化 CPU：{Format(report.SamplingCpuPercent)} %。");
        text.AppendLine($"- 峰值工作集（包含启动）：{Format(report.PeakWorkingSetBytes / 1024d / 1024)} MiB。");
        if (report.Samples.Count > 0)
        {
            text.AppendLine($"- 单次采集平均/最大耗时：{Format(report.Samples.Average(sample => sample.CollectionMilliseconds))} / {Format(report.Samples.Max(sample => sample.CollectionMilliseconds))} ms。");
        }
        text.AppendLine("这些是当前场景的短时测量，不能替代目标电脑至少 24 小时运行和七天试用验收。");
        text.AppendLine().AppendLine("## 指标能力与缺失原因").AppendLine();
        text.AppendLine("| 设备 | 指标 | 单位 | 有效/总样本 | 最后有效值 | 最后状态与原因 | 来源 |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var group in report.Samples.SelectMany(sample => sample.Metrics)
            .GroupBy(metric => (metric.DeviceId, metric.Key)).OrderBy(group => group.Key.DeviceId).ThenBy(group => group.Key.Key))
        {
            var last = group.Last();
            var valid = group.Where(metric => metric.Status == MetricStatus.Available).ToArray();
            text.AppendLine($"| {Cell(last.DeviceId)} | {Cell(last.Name)} ({Cell(last.Key)}) | {Cell(last.Unit)} | {valid.Length}/{report.Samples.Count} | {Format(valid.LastOrDefault()?.Value)} | {last.Status}: {Cell(last.Reason ?? "")} | {Cell(last.Source)} |");
        }
        text.AppendLine().AppendLine("## 硬件清单").AppendLine();
        text.AppendLine("物理磁盘容量与分区容量分别展示，不能把两者相加。显存容量从传感器读取，不采用存在 32 位容量限制的 WMI AdapterRAM。").AppendLine();
        text.AppendLine("| 物理盘 | 型号 | 容量 GiB |").AppendLine("| --- | --- | --- |");
        foreach (var disk in report.Inventory.PhysicalDisks)
            text.AppendLine($"| {Cell(disk.Id)} | {Cell(disk.Name)} | {Format(disk.SizeBytes / (double?)(1024L * 1024 * 1024))} |");
        text.AppendLine().AppendLine("| 分区 | 对应物理分区 | 总量 GiB | 空闲 GiB |").AppendLine("| --- | --- | --- | --- |");
        foreach (var volume in report.Inventory.Volumes)
            text.AppendLine($"| {Cell(volume.Id)} | {Cell(string.Join(", ", volume.PartitionIds))} | {Format(volume.SizeBytes / (double?)(1024L * 1024 * 1024))} | {Format(volume.FreeBytes / (double?)(1024L * 1024 * 1024))} |");
        var virtualAdapters = report.Inventory.DisplayAdapters.Where(adapter => !adapter.IsHardwareGpu).ToArray();
        text.AppendLine().AppendLine("已单独列为软件/虚拟显示适配器，不作为 GPU：" + string.Join("；", virtualAdapters.Select(adapter => Cell(adapter.Name))) + "。");
        text.AppendLine().AppendLine("## 错误记录").AppendLine();
        var issues = report.Inventory.Issues.Concat(report.SensorsBefore.Issues).Concat(report.SensorsAfter.Issues)
            .Concat(report.Samples.SelectMany(sample => sample.Issues)).Distinct().ToArray();
        if (issues.Length == 0) text.AppendLine("未记录采集异常。缺失指标仍需按能力表处理。");
        foreach (var issue in issues)
            text.AppendLine($"- {Cell(issue.Source)}：{issue.Status}，{Cell(issue.Reason)}。");
        text.AppendLine().AppendLine("不采集屏幕、文件内容、命令行、MAC/IP、硬件序列号；报告只写入本地，不进行网络上传。");
        return text.ToString();
    }

    private static string DriverText(SensorEnvironment environment) => environment.Drivers.Count == 0
        ? "未检测到相关驱动服务" : string.Join("，", environment.Drivers.Select(driver => $"{Cell(driver.Name)} ({Cell(driver.State)})"));
    private static string Format(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "未获取";
    private static string Cell(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
