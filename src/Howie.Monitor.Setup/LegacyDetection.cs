using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Setup;

public static class LegacyDetection
{
    public static (AgentConfiguration Configuration, LegacyAgent Process)? Find()
    {
        using var searcher = new ManagementObjectSearcher("SELECT ProcessId,CreationDate,ExecutablePath,CommandLine FROM Win32_Process WHERE Name='Howie.Monitor.Agent.exe'");
        using var rows = searcher.Get();
        foreach (var row in rows.Cast<ManagementObject>())
        {
            using (row)
            {
                string? executable = row["ExecutablePath"] as string, command = row["CommandLine"] as string;
                if (executable is null || command is null || executable.StartsWith(SetupPaths.ProgramRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                var match = Regex.Match(command, "(?:^|\\s)--config(?:=|\\s+)(?:\"(?<path>[^\"]+)\"|(?<path>\\S+))", RegexOptions.CultureInvariant);
                if (!match.Success) continue;
                string file = Path.GetFullPath(match.Groups["path"].Value);
                if (!File.Exists(file) || new FileInfo(file).Length > 65536) continue;
                var configuration = SetupPaths.Read<AgentConfiguration>(file);
                if (configuration is null || !Uri.TryCreate(configuration.ServerUrl, UriKind.Absolute, out var uri) || uri.AbsoluteUri != SetupPaths.ServerUrl) continue;
                var created = ManagementDateTimeConverter.ToDateTime((string)row["CreationDate"]).ToUniversalTime();
                return (configuration, new(Convert.ToInt32(row["ProcessId"]), created, executable, file));
            }
        }
        return null;
    }
    public static bool StopVerified(LegacyAgent old)
    {
        try
        {
            using var process = Process.GetProcessById(old.ProcessId);
            if (Math.Abs((process.StartTime.ToUniversalTime() - old.StartedAt.UtcDateTime).TotalSeconds) > 1
                || !string.Equals(process.MainModule?.FileName, old.Executable, StringComparison.OrdinalIgnoreCase)) return false;
            process.Kill(); process.WaitForExit(5000); return process.HasExited;
        }
        catch (ArgumentException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
