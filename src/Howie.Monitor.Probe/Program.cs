using System.Diagnostics;
using System.Reflection;
using Howie.Monitor.Collector;
using Howie.Monitor.Contracts;
using Howie.Monitor.Probe;

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine("Howie Monitor 只读探测：\n"
        + "  --samples 15 --interval-seconds 2 --label development|target\n"
        + "  --scenario unspecified|idle|game|work --output <新的报告目录>\n"
        + "生成 JSON 和 Markdown 报告；不安装驱动、不设置开机启动、不上报云端。\n"
        + "Ctrl+C 保存已经采集的样本并退出。");
    return 0;
}
if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("当前探测程序仅支持 Windows。");
    return 1;
}

try
{
    var options = ProbeOptions.Parse(args);
    if (Directory.Exists(options.OutputDirectory)
        && Directory.EnumerateFileSystemEntries(options.OutputDirectory).Any())
    {
        throw new IOException("输出目录已有内容，请指定新的目录，避免覆盖历史报告。");
    }
    Directory.CreateDirectory(options.OutputDirectory);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, evt) => { evt.Cancel = true; cancellation.Cancel(); };
    var startedAt = DateTimeOffset.UtcNow;
    var startup = Stopwatch.StartNew();
    Console.WriteLine("正在读取硬件清单和传感器能力……");
    var inventory = InventoryReader.Read();
    var sensorsBefore = InventoryReader.ReadSensorEnvironment();
    using var system = new WindowsSystemCollector(inventory);
    using var sensors = new SensorCollector(sensorsBefore);
    startup.Stop();

    using var process = Process.GetCurrentProcess();
    var samples = new List<ProbeSample>(options.Samples);
    var sampling = Stopwatch.StartNew();
    double initialCpu = process.TotalProcessorTime.TotalSeconds;
    double previousCpu = initialCpu;
    double previousElapsed = 0;
    long peakWorkingSet = 0;
    int logicalProcessors = inventory.Cpus.Sum(cpu => cpu.LogicalProcessors ?? 0);
    if (logicalProcessors == 0) logicalProcessors = Environment.ProcessorCount;
    for (int i = 0; i < options.Samples && !cancellation.IsCancellationRequested; i++)
    {
        var sampledAt = DateTimeOffset.UtcNow;
        var collection = Stopwatch.StartNew();
        var systemResult = system.Collect();
        var sensorResult = sensors.Collect();
        collection.Stop();
        process.Refresh();
        double currentCpu = process.TotalProcessorTime.TotalSeconds;
        double elapsed = sampling.Elapsed.TotalSeconds;
        double? overhead = i == 0 ? null : CounterMath.ProcessCpu(currentCpu - previousCpu,
            elapsed - previousElapsed, logicalProcessors);
        peakWorkingSet = Math.Max(peakWorkingSet, process.PeakWorkingSet64);
        samples.Add(new(i, sampledAt, Environment.TickCount64 / 1000d,
            collection.Elapsed.TotalMilliseconds, overhead, process.WorkingSet64,
            [.. systemResult.Metrics, .. sensorResult.Metrics], [.. systemResult.Issues, .. sensorResult.Issues]));
        previousCpu = currentCpu;
        previousElapsed = elapsed;
        Console.WriteLine($"样本 {i + 1}/{options.Samples}：{collection.Elapsed.TotalMilliseconds:0.0} ms，"
            + $"有效指标 {samples[^1].Metrics.Count(metric => metric.Status == MetricStatus.Available)}，"
            + $"工作集 {process.WorkingSet64 / 1024d / 1024:0.0} MiB");
        if (i + 1 < options.Samples)
        {
            var delay = TimeSpan.FromSeconds(options.IntervalSeconds) - collection.Elapsed;
            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay, cancellation.Token); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
            }
        }
    }
    sampling.Stop();
    double? samplingCpu = CounterMath.ProcessCpu(process.TotalProcessorTime.TotalSeconds - initialCpu,
        sampling.Elapsed.TotalSeconds, logicalProcessors);
    var sensorsAfter = InventoryReader.ReadSensorEnvironment();
    bool matchesReference = inventory.Cpus.Any(cpu => cpu.Name.Contains("9950X", StringComparison.OrdinalIgnoreCase))
        && inventory.DisplayAdapters.Any(gpu => gpu.IsHardwareGpu
            && gpu.Name.Contains("5070", StringComparison.OrdinalIgnoreCase)
            && gpu.Name.Contains("Ti", StringComparison.OrdinalIgnoreCase));
    var report = new ProbeReport("1", Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
        Environment.Version.ToString(), startedAt, DateTimeOffset.UtcNow, options, matchesReference,
        inventory, sensorsBefore, sensorsAfter, startup.Elapsed.TotalMilliseconds, sampling.Elapsed.TotalSeconds,
        samplingCpu, peakWorkingSet, cancellation.IsCancellationRequested, samples);
    await ReportWriter.Write(report);
    Console.WriteLine($"报告已保存：{options.OutputDirectory}");
    return cancellation.IsCancellationRequested ? 2 : 0;
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    Console.Error.WriteLine($"探测未完成：{ex.Message}");
    return 1;
}
