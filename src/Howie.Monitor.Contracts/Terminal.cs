using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Howie.Monitor.Contracts;

public sealed record TerminalAccess(bool Enabled, string UserSid);
public sealed record TerminalCapability(string AgentSessionId, bool Enabled, bool Available,
    string? Account, string? UserSid, string? Reason, string? LogonId = null);
public sealed record TerminalStatus(bool Connected, bool Available, string? Account, string? Reason, string? ActiveSessionId);
public sealed record TerminalCreateRequest(string ComputerId, string Password, int Columns, int Rows);
public sealed record TerminalSessionCreated(string SessionId, DateTimeOffset ConnectBefore);
public sealed record TerminalOffer(string SessionId, string ComputerId, string AgentSessionId, int Columns, int Rows);
public sealed record TerminalPollResponse(TerminalOffer? Offer);
public sealed record TerminalFrame(string Type, string? Data = null, int Columns = 0, int Rows = 0,
    string? Account = null, string? UserSid = null, bool Administrator = false, string? LogonId = null);

public static class TerminalProtocol
{
    public const int MaxFrameBytes = 65536;
    public static bool ValidSize(int columns, int rows) => columns is >= 20 and <= 400 && rows is >= 5 and <= 200;
    public static bool ValidUser(string? sid) => sid is { Length: > 8 and <= 184 }
        && (sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.StartsWith("S-1-12-1-", StringComparison.Ordinal))
        && sid.All(character => char.IsAsciiDigit(character) || character is 'S' or '-');
    public static bool ValidBrowserFrame(TerminalFrame frame) => frame.Type switch
    {
        "input" => frame.Data is { Length: > 0 and <= 16384 },
        "resize" => ValidSize(frame.Columns, frame.Rows),
        "close" => true,
        "ack" => true,
        _ => false
    };

    public static async Task<TerminalFrame?> Receive(WebSocket socket, CancellationToken token)
    {
        using var content = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || content.Length + result.Count > MaxFrameBytes)
                throw new InvalidDataException("终端消息类型或大小不正确。");
            content.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        try { return JsonSerializer.Deserialize<TerminalFrame>(content.GetBuffer().AsSpan(0, (int)content.Length), ProtocolJson.Options)
            ?? throw new InvalidDataException("终端消息为空。"); }
        catch (JsonException error) { throw new InvalidDataException("终端消息格式不正确。", error); }
    }

    public static async Task Send(WebSocket socket, TerminalFrame frame, CancellationToken token)
    {
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(frame, ProtocolJson.Options);
        if (content.Length > MaxFrameBytes) throw new InvalidDataException("终端消息过大。");
        await socket.SendAsync(content.AsMemory(), WebSocketMessageType.Text, true, token);
    }

    public static async Task Close(WebSocket socket, string reason, Task? pendingRead = null)
    {
        while (Encoding.UTF8.GetByteCount(reason) > 120) reason = reason[..^1];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, timeout.Token);
                if (pendingRead is not null) await Task.WhenAny(pendingRead, Task.Delay(1000, timeout.Token));
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { socket.Abort(); }
    }
}
