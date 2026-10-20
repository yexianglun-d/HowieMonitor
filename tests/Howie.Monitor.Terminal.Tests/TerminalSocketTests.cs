using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Howie.Monitor.Contracts;
using Howie.Monitor.Server;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Howie.Monitor.Terminal.Tests;

// 启动真实 Kestrel 与 Cookie/CSRF 中间件；采集端仅模拟协议，不冒充 Windows 提权验证。
public sealed class TerminalSocketTests
{
    [Fact] public async Task RealServerEnforcesAuthenticationAndRelaysOnlyTheBoundReadySession()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var token = deadline.Token;
        string directory = Path.Combine(Path.GetTempPath(), "howie-socket-" + Guid.NewGuid().ToString("N"));
        const string password = "isolated-terminal-test-password";
        var credentials = new CredentialStore(directory); Assert.True(credentials.Initialize(password));
        string deviceId = credentials.Device.DeviceId, agentSession = Guid.NewGuid().ToString();
        var telemetry = new TelemetryStore(directory);
        telemetry.InitializeEnrollment(credentials.Device, new EphemeralDataProtectionProvider().CreateProtector("test"));
        Assert.Null(telemetry.RegisterAuthenticated("Bearer " + credentials.Device.Token, new(deviceId, "explicit-test-computer",
            new("Windows", "11", [], [], [], [], [], [], []), new(false, false, null, false, 2, [], []), "test", "boot", agentSession), DateTimeOffset.UtcNow).Error);
        using var port = new TcpListener(IPAddress.Loopback, 0); port.Start(); int number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        var origin = new Uri($"http://127.0.0.1:{number}");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { typeof(TerminalBroker).Assembly.Location, "--local-development", "true", "--server-url", origin.ToString(), "--data-dir", directory }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(token); var stderr = process.StandardError.ReadToEndAsync(token);
        var cookies = new CookieContainer(); using var handler = new HttpClientHandler { CookieContainer = cookies };
        using var client = new HttpClient(handler) { BaseAddress = origin };
        using var agentHttp = new HttpClient { BaseAddress = origin };
        agentHttp.DefaultRequestHeaders.Authorization = new("Bearer", credentials.Device.Token);
        try
        {
            bool healthy = false;
            for (int i = 0; i < 100; i++)
            {
                try { healthy = (await client.GetAsync("/health/live", token)).IsSuccessStatusCode; } catch (HttpRequestException) { }
                if (healthy || process.HasExited) break;
                await Task.Delay(50, token);
            }
            Assert.True(healthy, process.HasExited ? await stderr : "server startup timed out");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/terminal/status?computerId={deviceId}", token)).StatusCode);
            async Task Csrf()
            {
                using var response = await client.GetAsync("/api/auth/csrf", token); response.EnsureSuccessStatusCode();
                var value = await response.Content.ReadFromJsonAsync<JsonElement>(token);
                client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", value.GetProperty("token").GetString());
            }
            await Csrf();
            using (var login = await client.PostAsJsonAsync("/api/auth/login", new { password }, token)) login.EnsureSuccessStatusCode();
            await Csrf();
            var capability = new TerminalCapability(agentSession, true, true, "PC\\admin", "S-1-5-21-1-2-3-1001", null, "1:abcdef");
            async Task<TerminalPollResponse> Poll()
            {
                using var response = await agentHttp.PostAsJsonAsync("/api/ingest/terminal/poll", capability, token);
                response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<TerminalPollResponse>(token))!;
            }
            async Task<TerminalSessionCreated> Create()
            {
                using var response = await client.PostAsJsonAsync("/api/terminal/sessions", new TerminalCreateRequest(deviceId, password, 80, 24), token);
                response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<TerminalSessionCreated>(token))!;
            }
            ClientWebSocket Browser(string? header = null)
            {
                var socket = new ClientWebSocket(); socket.Options.Cookies = cookies; socket.Options.SetRequestHeader("Origin", header ?? origin.GetLeftPart(UriPartial.Authority)); return socket;
            }
            Uri BrowserUrl(string id, string? computer = null) => new($"ws://127.0.0.1:{number}/api/terminal/sessions/{id}/socket?computerId={computer ?? deviceId}");
            async Task<ClientWebSocket> Agent(string id)
            {
                var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Authorization", "Bearer " + credentials.Device.Token);
                await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{number}/api/ingest/terminal/sessions/{id}/socket"), token); return socket;
            }
            TerminalFrame Ready(bool elevated = true) => new("ready", Account: capability.Account, UserSid: capability.UserSid, Administrator: elevated, LogonId: capability.LogonId);
            await Poll();
            using (var wrong = await client.PostAsJsonAsync("/api/terminal/sessions", new TerminalCreateRequest(deviceId, "wrong", 80, 24), token)) Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            using (var noCsrf = await client.PostAsJsonAsync("/api/terminal/sessions", new TerminalCreateRequest(deviceId, password, 80, 24), token)) Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
            await Csrf();
            var created = await Create(); Assert.Null((await Poll()).Offer);
            using (var foreign = Browser("https://foreign.example")) await Assert.ThrowsAsync<WebSocketException>(() => foreign.ConnectAsync(BrowserUrl(created.SessionId), token));
            using (var otherComputer = Browser()) await Assert.ThrowsAsync<WebSocketException>(() => otherComputer.ConnectAsync(BrowserUrl(created.SessionId, "different-computer"), token));
            using (var browser = Browser())
            {
                await browser.ConnectAsync(BrowserUrl(created.SessionId), token); Assert.Equal(created.SessionId, (await Poll()).Offer!.SessionId);
                using var agent = await Agent(created.SessionId);
                await TerminalProtocol.Send(agent, Ready(), token);
                Assert.Equal("ready", (await TerminalProtocol.Receive(browser, token))!.Type);
                await TerminalProtocol.Send(browser, new("input", "Get-Location\r"), token);
                Assert.Equal("Get-Location\r", (await TerminalProtocol.Receive(agent, token))!.Data);
                await TerminalProtocol.Send(browser, new("resize", Columns: 120, Rows: 30), token);
                Assert.Equal(120, (await TerminalProtocol.Receive(agent, token))!.Columns);
                await TerminalProtocol.Send(agent, new("output", "测试中文 😀\r\n"), token);
                Assert.Equal("测试中文 😀\r\n", (await TerminalProtocol.Receive(browser, token))!.Data);
                await TerminalProtocol.Send(browser, new("ack"), token);
                await browser.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "leave", token);
                Assert.Null(await TerminalProtocol.Receive(agent, token));
            }
            await Poll(); var refused = await Create();
            using (var browser = Browser())
            {
                await browser.ConnectAsync(BrowserUrl(refused.SessionId), token); await Poll(); using var agent = await Agent(refused.SessionId);
                await TerminalProtocol.Send(agent, Ready(false), token);
                Assert.Null(await TerminalProtocol.Receive(browser, token));
            }
            await Poll(); var revoked = await Create();
            using (var browser = Browser())
            {
                await browser.ConnectAsync(BrowserUrl(revoked.SessionId), token); await Poll(); using var agent = await Agent(revoked.SessionId);
                await TerminalProtocol.Send(agent, Ready(), token); await TerminalProtocol.Receive(browser, token);
                using (var logout = await client.PostAsync("/api/auth/logout", null, token)) logout.EnsureSuccessStatusCode();
                Assert.Null(await TerminalProtocol.Receive(agent, token)); Assert.Null(await TerminalProtocol.Receive(browser, token));
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true);
        }
    }
}
