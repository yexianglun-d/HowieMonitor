using System.Net;
using Howie.Monitor.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Howie.Monitor.Server.Tests;

public sealed class HostingTests
{
    [Theory]
    [InlineData(true, "http://0.0.0.0:5080", null)]
    [InlineData(false, "http://localhost:5080", null)]
    [InlineData(false, "http://0.0.0.0:5080", "https://monitor.example.com")]
    [InlineData(false, "http://127.0.0.1:5080", "http://monitor.example.com")]
    [InlineData(false, "http://127.0.0.1:5080/path", "https://monitor.example.com")]
    [InlineData(false, "http://127.0.0.1:5080", "https://user:password@monitor.example.com")]
    public void UnsafeHostingConfigurationIsRejected(bool local, string listen, string? external) =>
        Assert.Throws<InvalidOperationException>(() => ServerHosting.Create(local, listen, external));

    [Theory]
    [InlineData("127.0.0.1", true)] [InlineData("::1", true)] [InlineData("10.0.0.2", false)]
    public async Task ForwardingOnlyTrustsLoopbackProxy(string proxy, bool trusted)
    {
        var hosting = ServerHosting.Create(false, "http://127.0.0.1:5080", "https://monitor.example.com");
        var context = new DefaultHttpContext(); context.Connection.RemoteIpAddress = IPAddress.Parse(proxy);
        context.Request.Scheme = "http"; context.Request.Host = new("monitor.example.com");
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.14";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(hosting.Forwarding()));
        await middleware.Invoke(context);
        Assert.Equal(trusted ? "https" : "http", context.Request.Scheme);
        Assert.Equal(trusted ? "203.0.113.14" : proxy, context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal(trusted, hosting.Allows(context));
        context.Request.Host = new("other.example.com"); Assert.False(hosting.Allows(context));
    }

    [Fact] public void OnlyLoopbackHealthProbeCanUseBackendHttp()
    {
        var hosting = ServerHosting.Create(false, "http://127.0.0.1:5080", "https://monitor.example.com");
        var context = new DefaultHttpContext(); context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Scheme = "http"; context.Request.Host = new("127.0.0.1:5080"); context.Request.Path = "/health/live";
        Assert.True(hosting.Allows(context));
        context.Request.Path = "/api/auth/session"; Assert.False(hosting.Allows(context));
        context.Request.Path = "/health/live"; context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.14");
        Assert.False(hosting.Allows(context));
    }
}
