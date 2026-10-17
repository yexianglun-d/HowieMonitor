namespace Howie.Monitor.Collector;

public static class CounterMath
{
    public static double? Rate(long previous, long current, double seconds)
    {
        // 重置计数或失去有效时间基线后，不能把负差值显示成正常流量。
        if (previous < 0 || current < previous || !double.IsFinite(seconds) || seconds <= 0)
        {
            return null;
        }

        double value = (current - previous) / seconds;
        return double.IsFinite(value) ? value : null;
    }

    public static double? ProcessCpu(double cpuSeconds, double elapsedSeconds, int logicalProcessors)
    {
        if (cpuSeconds < 0 || !double.IsFinite(cpuSeconds) || !double.IsFinite(elapsedSeconds)
            || elapsedSeconds <= 0 || logicalProcessors <= 0)
        {
            return null;
        }

        double percent = cpuSeconds / elapsedSeconds / logicalProcessors * 100;
        return double.IsFinite(percent) && percent <= 100 ? percent : null;
    }
}
