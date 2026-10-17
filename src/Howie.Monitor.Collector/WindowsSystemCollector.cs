using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Collector;

public sealed class WindowsSystemCollector : IDisposable
{
    private readonly Inventory _inventory;
    private readonly Dictionary<string, (long Received, long Sent, long Timestamp)> _networkBaseline = [];
    private readonly List<(string Key, string Name, string Unit, nint Handle, uint Error)> _counters = [];
    private nint _query;
    private int _collections;
    private readonly uint _openError;

    public WindowsSystemCollector(Inventory inventory)
    {
        _inventory = inventory;
        _openError = PdhOpenQueryW(null, 0, out _query);
        Add("cpu.usage", "CPU 占用", "%", @"\Processor Information(*)\% Processor Time");
        Add("disk.read", "磁盘读取", "B/s", @"\PhysicalDisk(*)\Disk Read Bytes/sec");
        Add("disk.write", "磁盘写入", "B/s", @"\PhysicalDisk(*)\Disk Write Bytes/sec");
        Add("disk.activity", "磁盘活动时间", "%", @"\PhysicalDisk(*)\% Idle Time");
        Add("disk.response", "磁盘平均传输响应时间", "ms", @"\PhysicalDisk(*)\Avg. Disk sec/Transfer");
    }

    public (IReadOnlyList<MetricReading> Metrics, IReadOnlyList<CollectionIssue> Issues) Collect()
    {
        List<MetricReading> metrics = [];
        List<CollectionIssue> issues = [];
        uint collectError = _openError == 0 ? PdhCollectQueryData(_query) : _openError;
        _collections++;
        foreach (var counter in _counters)
        {
            uint error = counter.Error != 0 ? counter.Error : collectError;
            var values = error == 0 ? ReadArray(counter.Handle, out error) : [];
            var relevant = values.Where(item => counter.Key == "cpu.usage"
                ? !item.Name.EndsWith(",_Total", StringComparison.OrdinalIgnoreCase)
                : item.Name != "_Total").ToArray();
            if (error != 0 || relevant.Length == 0)
            {
                var status = _collections == 1 ? MetricStatus.NotReady : MetricStatus.Failed;
                string reason = $"PDH 尚无有效样本或计数器不可用，状态码 0x{error:X8}。";
                if (counter.Key == "cpu.usage")
                {
                    metrics.Add(MetricReading.Missing(counter.Key, "cpu/total", counter.Name,
                        counter.Unit, "Windows PDH", status, reason));
                }
                else
                {
                    foreach (var disk in _inventory.PhysicalDisks)
                    {
                        metrics.Add(MetricReading.Missing(counter.Key, disk.Id, counter.Name,
                            counter.Unit, "Windows PDH", status, reason));
                    }
                }
                continue;
            }

            foreach (var item in relevant)
            {
                bool totalCpu = counter.Key == "cpu.usage" && item.Name == "_Total";
                string deviceId = counter.Key == "cpu.usage"
                    ? totalCpu ? "cpu/total" : "cpu/logical/" + item.Name
                    : DiskId(item.Name);
                string key = counter.Key == "cpu.usage" && !totalCpu ? "cpu.logical.usage" : counter.Key;
                string source = "Windows PDH: " + item.Name;
                if (item.Status is not (0 or 1))
                {
                    metrics.Add(MetricReading.Missing(key, deviceId, counter.Name, counter.Unit,
                        source, _collections == 1 ? MetricStatus.NotReady : MetricStatus.Failed,
                        $"计数器返回状态码 0x{item.Status:X8}。"));
                    continue;
                }

                double value = counter.Key switch
                {
                    "disk.activity" => 100 - item.Value,
                    "disk.response" => item.Value * 1000,
                    _ => item.Value
                };
                // 第一轮只建立时间基线，PDH 即使返回零也不能将它当作有效占用。
                metrics.Add(_collections == 1
                    ? MetricReading.Missing(key, deviceId, counter.Name, counter.Unit,
                        source, MetricStatus.NotReady, "首次采样正在建立计数器时间基线。")
                    : MetricReading.Present(key, deviceId, counter.Name, value, counter.Unit, source));
            }
        }

        ReadMemory(metrics, issues);
        ReadNetworks(metrics, issues);
        ReadVolumes(metrics, issues);
        return (metrics, issues);
    }

    private void Add(string key, string name, string unit, string path)
    {
        nint handle = 0;
        uint error = _openError != 0 ? _openError : PdhAddEnglishCounterW(_query, path, 0, out handle);
        _counters.Add((key, name, unit, handle, error));
    }

    private string DiskId(string instance)
    {
        return uint.TryParse(instance.Split(' ')[0], out uint index)
            ? _inventory.PhysicalDisks.FirstOrDefault(disk => disk.Index == index)?.Id ?? "disk/" + index
            : "disk/counter/" + instance;
    }

    private static List<(string Name, double Value, uint Status)> ReadArray(nint counter, out uint error)
    {
        uint bytes = 0;
        uint count = 0;
        error = PdhGetFormattedCounterArrayW(counter, 0x200, ref bytes, ref count, 0);
        if (error != 0x800007D2 || bytes == 0 || bytes > 4 * 1024 * 1024)
        {
            return [];
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            error = PdhGetFormattedCounterArrayW(counter, 0x200, ref bytes, ref count, buffer);
            if (error != 0)
            {
                return [];
            }

            List<(string Name, double Value, uint Status)> values = [];
            int size = Marshal.SizeOf<CounterItem>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + i * size);
                values.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value.Value, item.Value.Status));
            }
            return values;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ReadMemory(List<MetricReading> metrics, List<CollectionIssue> issues)
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory) || memory.TotalPhysical == 0)
        {
            string reason = $"GlobalMemoryStatusEx 无有效结果，错误码 {Marshal.GetLastWin32Error()}。";
            issues.Add(new("GlobalMemoryStatusEx", MetricStatus.Failed, reason));
            foreach (var (key, unit) in new[] { ("memory.total", "B"), ("memory.available", "B"),
                ("memory.used", "B"), ("memory.usage", "%") })
            {
                metrics.Add(MetricReading.Missing(key, "memory", key, unit,
                    "GlobalMemoryStatusEx", MetricStatus.Failed, reason));
            }
            return;
        }

        ulong used = memory.TotalPhysical - memory.AvailablePhysical;
        metrics.Add(MetricReading.Present("memory.total", "memory", "物理内存总量", memory.TotalPhysical, "B", "GlobalMemoryStatusEx"));
        metrics.Add(MetricReading.Present("memory.available", "memory", "可用物理内存", memory.AvailablePhysical, "B", "GlobalMemoryStatusEx"));
        metrics.Add(MetricReading.Present("memory.used", "memory", "已用物理内存", used, "B", "GlobalMemoryStatusEx"));
        metrics.Add(MetricReading.Present("memory.usage", "memory", "物理内存占用", used * 100d / memory.TotalPhysical, "%", "GlobalMemoryStatusEx"));
    }

    private void ReadNetworks(List<MetricReading> metrics, List<CollectionIssue> issues)
    {
        Dictionary<string, (long Received, long Sent, long Timestamp)> next = [];
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => _inventory.Networks.Any(network =>
                    string.Equals(network.Id, nic.Id, StringComparison.OrdinalIgnoreCase))))
            {
                try
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                    {
                        AddRate("network.download", "下载速率", null, "网卡当前未连接。");
                        AddRate("network.upload", "上传速率", null, "网卡当前未连接。");
                        continue;
                    }

                    var stats = nic.GetIPStatistics();
                    long stamp = Stopwatch.GetTimestamp();
                    next[nic.Id] = (stats.BytesReceived, stats.BytesSent, stamp);
                    double? download = null;
                    double? upload = null;
                    if (_networkBaseline.TryGetValue(nic.Id, out var baseline))
                    {
                        double seconds = (stamp - baseline.Timestamp) / (double)Stopwatch.Frequency;
                        download = CounterMath.Rate(baseline.Received, stats.BytesReceived, seconds);
                        upload = CounterMath.Rate(baseline.Sent, stats.BytesSent, seconds);
                    }
                    AddRate("network.download", "下载速率", download, "首次采样、重连或计数重置，正在建立基线。");
                    AddRate("network.upload", "上传速率", upload, "首次采样、重连或计数重置，正在建立基线。");

                    void AddRate(string key, string name, double? value, string reason) =>
                        metrics.Add(value is { } rate
                            ? MetricReading.Present(key, nic.Id, nic.Name + " " + name, rate, "B/s", "NetworkInterface byte delta / monotonic elapsed time")
                            : MetricReading.Missing(key, nic.Id, nic.Name + " " + name, "B/s", "NetworkInterface", MetricStatus.NotReady, reason));
                }
                catch (NetworkInformationException ex)
                {
                    issues.Add(new(nic.Name, MetricStatus.Failed, ex.Message));
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            issues.Add(new("NetworkInterface", MetricStatus.Failed, ex.Message));
        }

        _networkBaseline.Clear();
        foreach (var item in next)
        {
            _networkBaseline.Add(item.Key, item.Value);
        }
    }

    private void ReadVolumes(List<MetricReading> metrics, List<CollectionIssue> issues)
    {
        foreach (var volume in _inventory.Volumes)
        {
            try
            {
                var drive = new DriveInfo(volume.Id);
                metrics.Add(MetricReading.Present("volume.total", volume.Id, "分区总空间", drive.TotalSize, "B", "DriveInfo"));
                metrics.Add(MetricReading.Present("volume.free", volume.Id, "分区可用空间", drive.AvailableFreeSpace, "B", "DriveInfo.AvailableFreeSpace"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var status = ex is UnauthorizedAccessException ? MetricStatus.PermissionDenied : MetricStatus.Failed;
                issues.Add(new(volume.Id, status, ex.Message));
                metrics.Add(MetricReading.Missing("volume.free", volume.Id, "分区可用空间", "B", "DriveInfo", status, ex.Message));
            }
        }
    }

    public void Dispose()
    {
        if (_query != 0)
        {
            PdhCloseQuery(_query);
            _query = 0;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public nint Name;
        public CounterValue Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? source, nuint data, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(nint query, string path, nuint data, out nint counter);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format,
        ref uint bytes, ref uint count, nint buffer);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(nint query);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
