using Howie.Monitor.Collector;
using Howie.Monitor.Contracts;
using Howie.Monitor.Probe;
using LibreHardwareMonitor.Hardware;
using Xunit;

namespace Howie.Monitor.Collector.Tests;

public sealed class CollectionRulesTests
{
    [Fact]
    public void NetworkRateUsesActualElapsedSeconds()
    {
        Assert.Equal(250, CounterMath.Rate(1000, 2000, 4));
    }

    [Theory]
    [InlineData(1000, 100, 2)]
    [InlineData(0, 100, 0)]
    [InlineData(0, 100, -1)]
    [InlineData(-1, 100, 2)]
    public void ResetOrInvalidBaselineHasNoRate(long previous, long current, double seconds)
    {
        Assert.Null(CounterMath.Rate(previous, current, seconds));
    }

    [Fact]
    public void CpuCostIsNormalizedAcrossLogicalProcessors()
    {
        Assert.Equal(5, CounterMath.ProcessCpu(2, 2, 20));
        Assert.Null(CounterMath.ProcessCpu(1, 0, 20));
        Assert.Null(CounterMath.ProcessCpu(1, 2, 0));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(101)]
    public void InvalidPercentagesStayMissing(double value)
    {
        var metric = MetricReading.Present("cpu.usage", "cpu", "CPU", value, "%", "test");
        Assert.Null(metric.Value);
        Assert.Equal(MetricStatus.Failed, metric.Status);
    }

    [Fact]
    public void RealZeroAndMissingValueRemainDifferent()
    {
        var zero = MetricReading.Present("network.upload", "nic", "上传", 0, "B/s", "test");
        var missing = MetricReading.Missing("cpu.temperature", "cpu", "温度", "°C", "test",
            MetricStatus.DriverMissing, "未检测到驱动");
        Assert.Equal(0, zero.Value);
        Assert.Equal(MetricStatus.Available, zero.Status);
        Assert.Null(missing.Value);
        Assert.Equal(MetricStatus.DriverMissing, missing.Status);
    }

    [Theory]
    [InlineData("PCI\\VEN_10DE&DEV_2C02", true)]
    [InlineData("PCI\\VEN_8086&DEV_46A6", true)]
    [InlineData("ROOT\\DISPLAY\\0000", false)]
    [InlineData("SWD\\OrayIddDriver", false)]
    public void SoftwareDisplayAdaptersAreNotHardwareGpus(string pnpId, bool expected)
    {
        Assert.Equal(expected, InventoryReader.IsHardwareGpu(pnpId));
    }

    [Theory]
    [InlineData(HardwareType.Cpu, SensorType.Temperature, true)]
    [InlineData(HardwareType.Cpu, SensorType.Clock, true)]
    [InlineData(HardwareType.GpuIntel, SensorType.Power, true)]
    [InlineData(HardwareType.GpuIntel, SensorType.Load, false)]
    [InlineData(HardwareType.GpuNvidia, SensorType.Power, false)]
    public void MsrMetricsRequirePawnIoButSystemAndVendorMetricsDoNot(
        HardwareType hardwareType, SensorType sensorType, bool expected)
    {
        Assert.Equal(expected, SensorCollector.RequiresPawnIo(hardwareType, sensorType));
    }

    [Theory]
    [InlineData(5, MetricStatus.PermissionDenied)]
    [InlineData(2, MetricStatus.DriverMissing)]
    [InlineData(3, MetricStatus.DriverMissing)]
    [InlineData(31, MetricStatus.Failed)]
    public void DriverOpenErrorsPreserveTheActualFailureReason(int error, MetricStatus status)
    {
        Assert.Equal(status, SensorCollector.DriverStatus(error));
    }

    [Theory]
    [InlineData("--interval-seconds", "NaN")]
    [InlineData("--interval-seconds", "Infinity")]
    [InlineData("--interval-seconds", "0")]
    [InlineData("--samples", "2")]
    [InlineData("--samples", "1801")]
    [InlineData("--label", "production")]
    [InlineData("--scenario", "automatic")]
    [InlineData("--unknown", "1")]
    public void InvalidArgumentsFailBeforeProbing(string key, string value)
    {
        Assert.Throws<ArgumentException>(() => ProbeOptions.Parse([key, value]));
    }
}
