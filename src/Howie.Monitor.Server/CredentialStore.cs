using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Server;

public sealed record DeviceCredential(string DeviceId, string Token);
public sealed record UserCredential(string PasswordHash, Dictionary<string, DateTimeOffset> Sessions);

public sealed class CredentialStore
{
    private readonly string _userFile;
    private readonly PasswordHasher<string> _hasher = new();
    private readonly object _gate = new();
    private UserCredential? _user;
    public DeviceCredential Device { get; }
    public bool IsSetup { get { lock (_gate) return _user is not null; } }

    public CredentialStore(string directory)
    {
        Directory.CreateDirectory(directory);
        string deviceFile = Path.Combine(directory, "device-credential.json");
        Device = File.Exists(deviceFile)
            ? JsonSerializer.Deserialize<DeviceCredential>(File.ReadAllText(deviceFile), ProtocolJson.Options)!
            : new("local-" + Guid.NewGuid().ToString("N"), Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
        if (!File.Exists(deviceFile)) WritePrivate(deviceFile, JsonSerializer.Serialize(Device, ProtocolJson.Options));
        _userFile = Path.Combine(directory, "user.json");
        if (File.Exists(_userFile))
        {
            string json = File.ReadAllText(_userFile);
            _user = JsonSerializer.Deserialize<UserCredential>(json, ProtocolJson.Options);
            if (_user is { Sessions: null })
            {
                using var legacy = JsonDocument.Parse(json);
                Dictionary<string, DateTimeOffset> sessions = [];
                if (legacy.RootElement.TryGetProperty("sessionStamp", out var stamp) && stamp.GetString() is { Length: > 0 } value)
                    sessions[value] = DateTimeOffset.UtcNow.AddHours(12);
                _user = _user with { Sessions = sessions };
            }
        }
    }

    public bool AuthenticateDevice(string? authorization)
    {
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..])), SHA256.HashData(Encoding.UTF8.GetBytes(Device.Token)));
    }

    public bool Initialize(string password)
    {
        lock (_gate)
        {
            if (_user is not null || password.Length is < 12 or > 128) return false;
            _user = new(_hasher.HashPassword("owner", password), []); Save(); return true;
        }
    }

    public string? Login(string password, bool setup)
    {
        lock (_gate)
        {
            if (setup)
            {
                if (!Initialize(password)) return null;
            }
            else if (_user is null || password.Length > 128
                || _hasher.VerifyHashedPassword("owner", _user.PasswordHash, password) == PasswordVerificationResult.Failed)
                return null;
            foreach (var expired in _user!.Sessions.Where(session => session.Value <= DateTimeOffset.UtcNow).Select(session => session.Key).ToArray())
                _user.Sessions.Remove(expired);
            if (_user.Sessions.Count >= 100) return null;
            string newStamp = Guid.NewGuid().ToString("N");
            _user.Sessions[newStamp] = DateTimeOffset.UtcNow.AddHours(12);
            Save();
            return newStamp;
        }
    }

    public bool ValidSession(string? stamp) { lock (_gate) return !string.IsNullOrEmpty(stamp) && _user?.Sessions.TryGetValue(stamp, out var expiry) == true && expiry > DateTimeOffset.UtcNow; }
    public void Logout(string? stamp) { lock (_gate) { if (stamp is not null && _user is not null && _user.Sessions.Remove(stamp)) Save(); } }
    private void Save() => WritePrivate(_userFile, JsonSerializer.Serialize(_user, ProtocolJson.Options));
    private static void WritePrivate(string path, string content)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, content);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, true);
    }
}
