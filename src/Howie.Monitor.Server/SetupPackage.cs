using System.Security.Cryptography;
using System.Text.Json;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Server;

public sealed record SetupDownload(bool Available, string Version, string? FileName, long? Bytes, string? Sha256, string? InstallCommand = null);
public sealed class SetupPackage(string path)
{
    private readonly object _gate = new();
    private DateTime _lastWrite;
    private long _length;
    private string? _hash;
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    public SetupDownload Info()
    {
        lock (_gate)
        {
            var file = new FileInfo(Path);
            if (!file.Exists) return new(false, "未标记", null, null, null);
            if (_hash is null || file.LastWriteTimeUtc != _lastWrite || file.Length != _length)
            {
                using var stream = file.OpenRead(); _hash = Convert.ToHexString(SHA256.HashData(stream));
                _lastWrite = file.LastWriteTimeUtc; _length = file.Length;
            }
            var release = ReadRelease(_hash, file.Length);
            return new(true, release?.Version ?? "未标记", "HowieMonitorSetup.exe", file.Length, _hash);
        }
    }
    public AgentRelease? Release()
    {
        lock (_gate)
        {
            var info = Info();
            return info.Sha256 is not null && info.Bytes.HasValue ? ReadRelease(info.Sha256, info.Bytes.Value) : null;
        }
    }
    private AgentRelease? ReadRelease(string hash, long bytes)
    {
        string manifest = Path + ".release.json";
        try
        {
            if (!File.Exists(manifest) || new FileInfo(manifest).Length > 4096) return null;
            var release = JsonSerializer.Deserialize<AgentRelease>(File.ReadAllText(manifest), ProtocolJson.Options);
            return release is { Valid: true } && release.Bytes == bytes && string.Equals(release.Sha256, hash, StringComparison.OrdinalIgnoreCase) ? release : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public FileStream? OpenVerified(string sha256)
    {
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) return null;
        lock (_gate)
        {
            if (!File.Exists(Path)) return null;
            // 校验和响应使用同一文件句柄，发布期间替换安装包也不会混用两个版本。
            var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            try
            {
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), sha256, StringComparison.OrdinalIgnoreCase))
                { stream.Dispose(); return null; }
                stream.Position = 0; return stream;
            }
            catch { stream.Dispose(); throw; }
        }
    }
}
