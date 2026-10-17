using System.Text.Json;
using System.Text.RegularExpressions;

namespace Howie.Monitor.Server;

public sealed record SendResult(string Status, string Detail);
public sealed class ServerChanSender : IDisposable
{
    private readonly HttpClient _client;
    public ServerChanSender() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }
    public ServerChanSender(HttpMessageHandler handler) => _client = new(handler) { Timeout = TimeSpan.FromSeconds(15) };
    public static bool ValidKey(string key) => Regex.IsMatch(key, "\\ASCT[A-Za-z0-9]{10,200}\\z", RegexOptions.CultureInvariant);
    public async Task<SendResult> Send(NotificationDispatch dispatch, CancellationToken cancellationToken)
    {
        if (!ValidKey(dispatch.SendKey)) return new("failed", "SendKey 格式错误，请重新保存。");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            { ["title"] = dispatch.Event.Title, ["desp"] = dispatch.Event.Description });
            // SendKey 位于官方 URL 路径中，不能使用会自动记录请求 URL 的客户端日志。
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://sctapi.ftqq.com/{dispatch.SendKey}.send") { Content = content };
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return new("failed", $"服务商返回 HTTP {(int)response.StatusCode}，请检查账号和额度。未自动重发。");
            if (response.Content.Headers.ContentLength > 16384) return new("unknown", "服务商响应超出预期，结果未知，未自动重发。");
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            byte[] buffer = new byte[16385]; int length = 0, read;
            while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token)) > 0) length += read;
            if (length > 16384) return new("unknown", "服务商响应超出预期，结果未知，未自动重发。");
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("code", out var code)
                || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out int number))
                return new("unknown", "服务商响应格式不正确，结果未知，未自动重发。");
            return number == 0 ? new("accepted", "Server酱已接受，请在微信确认实际收件。")
                : new("failed", $"Server酱拒绝请求（code={number}），请检查密钥、通道和额度。未自动重发。");
        }
        catch (OperationCanceledException) { return new("unknown", "请求超时或被中断，可能已经发送，未自动重发。"); }
        catch (HttpRequestException) { return new("unknown", "网络或证书请求失败，结果未知，未自动重发。"); }
        catch (JsonException) { return new("unknown", "服务商响应无法解析，结果未知，未自动重发。"); }
        catch (IOException) { return new("unknown", "服务商响应读取中断，结果未知，未自动重发。"); }
    }
    public void Dispose() => _client.Dispose();
}
