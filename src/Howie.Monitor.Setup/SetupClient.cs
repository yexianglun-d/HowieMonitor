using System.Net.Http.Headers;
using System.Net.Http.Json;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Setup;

public sealed class SetupClient : IDisposable
{
    private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false })
    { BaseAddress = new(SetupPaths.ServerUrl), Timeout = TimeSpan.FromSeconds(15) };
    public async Task Check(CancellationToken cancellationToken)
    { using var response = await _client.GetAsync("api/auth/session", cancellationToken); response.EnsureSuccessStatusCode(); }
    public async Task<EnrollmentCredential> Claim(EnrollmentClaim value, CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsJsonAsync("api/enrollment/claim", value, ProtocolJson.Options, cancellationToken);
        await Ensure(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<EnrollmentCredential>(ProtocolJson.Options, cancellationToken)
            ?? throw new InvalidOperationException("服务未返回绑定凭据。");
    }
    public async Task<EnrollmentProgress> Status(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/ingest/status"); request.Headers.Authorization = new("Bearer", token);
        using var response = await _client.SendAsync(request, cancellationToken); await Ensure(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<EnrollmentProgress>(ProtocolJson.Options, cancellationToken)
            ?? throw new InvalidOperationException("服务未返回接入状态。");
    }
    public async Task<EnrollmentProgress> Activate(InstallPlan plan, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/ingest/activate")
        { Content = JsonContent.Create(new EnrollmentActivation(plan.RequestId!), options: ProtocolJson.Options) };
        request.Headers.Authorization = new("Bearer", plan.Configuration.Token);
        using var response = await _client.SendAsync(request, cancellationToken); await Ensure(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<EnrollmentProgress>(ProtocolJson.Options, cancellationToken)
            ?? throw new InvalidOperationException("服务未返回激活结果。");
    }
    private static async Task Ensure(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new InvalidOperationException("接入凭据已过期或撤销，请返回网页重新生成绑定码。");
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests) throw new InvalidOperationException("操作次数较多，请等待一分钟后重试。");
        var error = await response.Content.ReadFromJsonAsync<SetupError>(ProtocolJson.Options, cancellationToken);
        throw new InvalidOperationException(error?.Error ?? "服务器拒绝请求，请稍后重试。");
    }
    public void Dispose() => _client.Dispose();
    private sealed record SetupError(string? Error);
}
