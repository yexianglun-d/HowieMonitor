using System.Globalization;

namespace Howie.Monitor.Probe;

public sealed record ProbeOptions(int Samples, double IntervalSeconds, string Label,
    string Scenario, string OutputDirectory)
{
    public static ProbeOptions Parse(string[] args)
    {
        int samples = 15;
        double interval = 2;
        string label = "development";
        string scenario = "unspecified";
        string output = Path.Combine("reports", "probe-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        for (int i = 0; i < args.Length; i++)
        {
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"参数 {args[i]} 缺少值。");
            }
            string value = args[++i];
            switch (args[i - 1])
            {
                case "--samples":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out samples)
                        || samples is < 3 or > 1800)
                        throw new ArgumentException("--samples 必须在 3–1800 之间。");
                    break;
                case "--interval-seconds":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out interval)
                        || !double.IsFinite(interval) || interval is < 1 or > 60)
                        throw new ArgumentException("--interval-seconds 必须是 1–60 之间的有限数值。");
                    break;
                case "--label":
                    if (value is not ("development" or "target"))
                        throw new ArgumentException("--label 只能是 development 或 target。");
                    label = value;
                    break;
                case "--scenario":
                    if (value is not ("idle" or "game" or "work" or "unspecified"))
                        throw new ArgumentException("--scenario 只能是 idle、game、work 或 unspecified。");
                    scenario = value;
                    break;
                case "--output":
                    output = value;
                    break;
                default:
                    throw new ArgumentException($"未知参数 {args[i - 1]}。");
            }
        }

        return new(samples, interval, label, scenario, Path.GetFullPath(output));
    }
}
