using System.Diagnostics;
using System.ComponentModel;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Collector;

public sealed class ProcessCollector
{
    private readonly Dictionary<(int Pid, long Started), (double Cpu, long Stamp)> _baselines = [];

    public IReadOnlyList<ProcessReading> Collect()
    {
        List<ProcessReading> readings = [];
        Dictionary<(int Pid, long Started), (double Cpu, long Stamp)> next = [];
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                int pid = process.Id;
                string name = "未获取";
                DateTimeOffset? started = null;
                long? workingSet = null;
                try
                {
                    name = process.ProcessName;
                    started = new DateTimeOffset(process.StartTime.ToUniversalTime());
                    workingSet = process.WorkingSet64;
                    double cpu = process.TotalProcessorTime.TotalSeconds;
                    long stamp = Stopwatch.GetTimestamp();
                    var identity = (pid, started.Value.UtcTicks);
                    double? percent = null;
                    if (_baselines.TryGetValue(identity, out var baseline))
                        percent = CounterMath.ProcessCpu(cpu - baseline.Cpu,
                            (stamp - baseline.Stamp) / (double)Stopwatch.Frequency, Environment.ProcessorCount);
                    next[identity] = (cpu, stamp);
                    readings.Add(new(pid, started, name, percent, workingSet,
                        percent.HasValue ? MetricStatus.Available : MetricStatus.NotReady,
                        percent.HasValue ? null : "首次采样或进程重新启动，正在建立基线。"));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    bool denied = ex is Win32Exception { NativeErrorCode: 5 };
                    readings.Add(new(pid, started, name, null, workingSet,
                        denied ? MetricStatus.PermissionDenied : MetricStatus.Failed,
                        denied ? "当前权限不能读取此进程。" : "进程已退出或读取失败。"));
                }
            }
        }
        _baselines.Clear();
        foreach (var pair in next) _baselines.Add(pair.Key, pair.Value);
        return readings;
    }
}
