using System.Security.Cryptography;

namespace Howie.Monitor.Server;

public sealed record SetupDownload(bool Available, string Version, string? FileName, long? Bytes, string? Sha256);
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
            if (!file.Exists) return new(false, "0.1.0", null, null, null);
            if (_hash is null || file.LastWriteTimeUtc != _lastWrite || file.Length != _length)
            {
                using var stream = file.OpenRead(); _hash = Convert.ToHexString(SHA256.HashData(stream));
                _lastWrite = file.LastWriteTimeUtc; _length = file.Length;
            }
            return new(true, "0.1.0", "HowieMonitorSetup.exe", file.Length, _hash);
        }
    }
}
