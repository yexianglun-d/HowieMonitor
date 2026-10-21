using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Howie.Monitor.Server;
using Xunit;

namespace Howie.Monitor.Terminal.Tests;

public sealed class SetupBootstrapTests
{
    [Fact] public void DownloadUsesTheVerifiedHandleEvenWhenThePublishedPackageIsReplaced()
    {
        string root = Path.Combine(Path.GetTempPath(), "howie-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "setup.exe"); File.WriteAllText(path, "first reviewed package");
            var package = new SetupPackage(path); var first = package.Info();
            Assert.Null(package.OpenVerified("../../not-a-hash"));
            Assert.Null(package.OpenVerified(new string('0', 64)));
            using (var stream = package.OpenVerified(first.Sha256!)!)
            {
                Assert.NotNull(stream);
                string replacement = Path.Combine(root, "new.exe"); File.WriteAllText(replacement, "second reviewed package");
                File.Move(replacement, path, true);
                using var reader = new StreamReader(stream); Assert.Equal("first reviewed package", reader.ReadToEnd());
            }
            Assert.Null(package.OpenVerified(first.Sha256!));
            Assert.NotEqual(first.Sha256, package.Info().Sha256);
            File.Delete(path); Assert.False(package.Info().Available); Assert.Null(package.OpenVerified(first.Sha256!));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact] public void ScriptPinsTheConfiguredOriginAndPackageWithoutCredentialsOrExecutionPolicyChanges()
    {
        var hosting = ServerHosting.Create(false, "http://127.0.0.1:5080", "https://monitor.example:8443/");
        string hash = new string('A', 64);
        string script = SetupBootstrap.Script(hosting, new(true, "0.1.0", "HowieMonitorSetup.exe", 123, hash));
        Assert.Contains($"https://monitor.example:8443/install/{hash}/HowieMonitorSetup.exe", script);
        Assert.Contains("Get-FileHash -LiteralPath $setup -Algorithm SHA256", script);
        Assert.Contains("-MaximumRedirection 0", script);
        Assert.Contains("-ArgumentList '--cli' -NoNewWindow -Wait -PassThru", script);
        Assert.Contains("GetFolderPath('ProgramFiles')", script);
        Assert.Contains("'https://monitor.example:8443/install.ps1'", SetupBootstrap.Command(hosting));
        Assert.DoesNotContain("ExecutionPolicy", script); Assert.DoesNotContain("Bearer", script);
        Assert.Throws<InvalidOperationException>(() => SetupBootstrap.Script(hosting, new(false, "0.1.0", null, null, null)));
    }

    [Fact] public async Task PublicBootstrapServesOnlyThePinnedPackageWhilePanelDataStillRequiresLogin()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40)); var token = deadline.Token;
        string root = Path.Combine(Path.GetTempPath(), "howie-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "setup.exe");
        byte[] payload = Encoding.UTF8.GetBytes("isolated test fixture, never executable");
        string hash = Convert.ToHexString(SHA256.HashData(payload));
        using var port = new TcpListener(IPAddress.Loopback, 0); port.Start(); int number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        var origin = new Uri($"http://127.0.0.1:{number}");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { typeof(SetupPackage).Assembly.Location, "--local-development", "true", "--server-url", origin.ToString(), "--data-dir", root, "--setup-package", path }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(token); var stderr = process.StandardError.ReadToEndAsync(token);
        using var client = new HttpClient { BaseAddress = origin };
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
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/install.ps1", token)).StatusCode);
            await File.WriteAllBytesAsync(path, payload, token);
            using (var script = await client.GetAsync("/install.ps1", token))
            {
                script.EnsureSuccessStatusCode(); Assert.Equal("text/plain", script.Content.Headers.ContentType?.MediaType);
                Assert.True(script.Headers.CacheControl?.NoStore);
                Assert.Contains(new Uri(origin, $"install/{hash}/HowieMonitorSetup.exe").ToString(), await script.Content.ReadAsStringAsync(token));
            }
            Assert.Equal(payload, await client.GetByteArrayAsync($"/install/{hash}/HowieMonitorSetup.exe", token));
            using (var request = new HttpRequestMessage(HttpMethod.Get, $"/install/{hash}/HowieMonitorSetup.exe"))
            {
                request.Headers.Range = new(0, 6);
                using var partial = await client.SendAsync(request, token); Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
                Assert.Equal(payload[..7], await partial.Content.ReadAsByteArrayAsync(token));
            }
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/install/{new string('0', 64)}/HowieMonitorSetup.exe", token)).StatusCode);
            await File.WriteAllTextAsync(path, "updated package", token);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/install/{hash}/HowieMonitorSetup.exe", token)).StatusCode);
            foreach (string endpoint in new[] { "/api/setup", "/api/setup/download", "/api/enrollment", "/api/computers", "/api/notifications" })
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(endpoint, token)).StatusCode);
            for (int i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/install.ps1", token)).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/install.ps1", token)).StatusCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr); Directory.Delete(root, true);
        }
    }
}
