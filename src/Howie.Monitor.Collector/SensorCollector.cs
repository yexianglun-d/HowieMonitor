using LibreHardwareMonitor.Hardware;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Collector;

public sealed class SensorCollector : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsStorageEnabled = true
    };
    private readonly SensorEnvironment _environment;
    private readonly List<CollectionIssue> _initialIssues = [];
    private readonly Dictionary<IHardware, string> _hardwareIds = [];

    public SensorCollector(SensorEnvironment environment)
    {
        _environment = environment;
        try
        {
            _computer.Open();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _initialIssues.Add(new("LibreHardwareMonitor.Open", ErrorStatus(ex), ex.Message));
        }
    }

    public (IReadOnlyList<MetricReading> Metrics, IReadOnlyList<CollectionIssue> Issues) Collect()
    {
        List<MetricReading> metrics = [];
        List<CollectionIssue> issues = [.. _initialIssues];
        foreach (var hardware in _computer.Hardware)
        {
            ReadHardware(hardware, metrics, issues);
        }

        Require("cpu.temperature", "cpu/total", "CPU 温度", "°C", HardwareType.Cpu, SensorType.Temperature);
        Require("gpu.temperature", "gpu", "GPU 温度", "°C", null, SensorType.Temperature);
        Require("gpu.usage", "gpu", "GPU 占用", "%", null, SensorType.Load);
        Require("gpu.memory", "gpu", "GPU 显存", "B", null, SensorType.SmallData);
        Require("disk.temperature", "disk", "磁盘温度", "°C", HardwareType.Storage, SensorType.Temperature);
        return (metrics, issues);

        void Require(string key, string deviceId, string name, string unit, HardwareType? type, SensorType sensorType)
        {
            var matching = _computer.Hardware.Where(hardware => type.HasValue
                ? hardware.HardwareType == type : IsGpu(hardware.HardwareType));
            var devices = matching.ToArray();
            var missingDevices = devices.Where(hardware => !hardware.Sensors.Any(sensor => sensor.SensorType == sensorType)).ToArray();
            if (devices.Length > 0 && missingDevices.Length == 0)
            {
                return;
            }

            var status = type == HardwareType.Cpu && !_environment.PawnIoDeviceAccessible
                ? DriverStatus(_environment.PawnIoOpenError) : MetricStatus.Unknown;
            string reason = type == HardwareType.Cpu && !_environment.PawnIoDeviceAccessible
                ? $"PawnIO 设备不可访问，错误码 {_environment.PawnIoOpenError}；当前未暴露该 CPU 传感器。"
                : "当前硬件库未暴露该类传感器；不能据此断言硬件不支持或权限不足。";
            if (devices.Length == 0)
                metrics.Add(MetricReading.Missing(key, deviceId, name, unit, "LibreHardwareMonitorLib 0.9.6", status, reason));
            foreach (var hardware in missingDevices)
                metrics.Add(MetricReading.Missing(key, _hardwareIds[hardware], name, unit,
                    "LibreHardwareMonitorLib 0.9.6: " + hardware.Name, status, reason));
        }
    }

    private void ReadHardware(IHardware hardware, List<MetricReading> metrics, List<CollectionIssue> issues)
    {
        if (!_hardwareIds.TryGetValue(hardware, out string? id))
        {
            // 本次探测使用局部编号，不把硬件库内部 PNP 路径或潜在序列号写入报告。
            id = $"sensor/{hardware.HardwareType}/{_hardwareIds.Count}";
            _hardwareIds.Add(hardware, id);
        }
        bool updated = true;
        string? failure = null;
        var failureStatus = MetricStatus.Failed;
        try
        {
            hardware.Update();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            updated = false;
            failure = ex.Message;
            failureStatus = ErrorStatus(ex);
            issues.Add(new(hardware.Name, failureStatus, failure));
        }

        foreach (var sensor in hardware.Sensors)
        {
            var unit = Unit(sensor.SensorType);
            if (unit is null)
            {
                continue;
            }

            string source = $"LibreHardwareMonitorLib 0.9.6: {hardware.Name}/{sensor.SensorType}";
            string key = $"{id}/{sensor.SensorType}/{sensor.Index}";
            // Intel 核显功耗同样来自 CPU MSR，驱动失效时硬件库可能给出零值。
            bool driverRequired = RequiresPawnIo(hardware.HardwareType, sensor.SensorType);
            if (!updated)
            {
                metrics.Add(MetricReading.Missing(key, id, sensor.Name, unit.Value.Unit, source, failureStatus, failure!));
            }
            else if (driverRequired && !_environment.PawnIoDeviceAccessible)
            {
                metrics.Add(MetricReading.Missing(key, id, sensor.Name, unit.Value.Unit, source,
                    DriverStatus(_environment.PawnIoOpenError),
                    $"PawnIO 设备不可访问，错误码 {_environment.PawnIoOpenError}；不采用可能来自失效驱动的数值。"));
            }
            else if (sensor.Value is { } value)
            {
                metrics.Add(MetricReading.Present(key, id, sensor.Name, value * unit.Value.Scale,
                    unit.Value.Unit, source));
            }
            else
            {
                metrics.Add(MetricReading.Missing(key, id, sensor.Name, unit.Value.Unit, source,
                    MetricStatus.Unknown, "传感器返回 null；具体原因需结合权限、驱动和硬件验证。"));
            }
        }

        foreach (var child in hardware.SubHardware)
        {
            ReadHardware(child, metrics, issues);
        }
    }

    private static bool IsGpu(HardwareType type) =>
        type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    public static bool RequiresPawnIo(HardwareType hardwareType, SensorType sensorType) =>
        hardwareType == HardwareType.Cpu
            && sensorType is SensorType.Temperature or SensorType.Power or SensorType.Voltage or SensorType.Clock
        || hardwareType == HardwareType.GpuIntel && sensorType == SensorType.Power;

    public static MetricStatus DriverStatus(int? error) => error switch
    {
        5 => MetricStatus.PermissionDenied,
        2 or 3 => MetricStatus.DriverMissing,
        _ => MetricStatus.Failed
    };

    private static (string Unit, double Scale)? Unit(SensorType type) => type switch
    {
        SensorType.Temperature => ("°C", 1),
        SensorType.Load => ("%", 1),
        SensorType.Clock => ("MHz", 1),
        SensorType.Power => ("W", 1),
        SensorType.Voltage => ("V", 1),
        SensorType.Data => ("B", 1024d * 1024 * 1024),
        SensorType.SmallData => ("B", 1024d * 1024),
        SensorType.Throughput => ("B/s", 1),
        _ => null
    };

    private static MetricStatus ErrorStatus(Exception ex) =>
        ex is UnauthorizedAccessException ? MetricStatus.PermissionDenied : MetricStatus.Failed;

    public void Dispose() => _computer.Close();
}
