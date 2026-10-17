using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Howie.Monitor.Server;

public sealed record ServerHosting(bool LocalDevelopment, Uri ListenUrl, Uri? PublicUrl)
{
    public bool BehindProxy => PublicUrl is not null;

    public static ServerHosting Create(bool localDevelopment, string? listenUrl, string? publicUrl)
    {
        if (!Uri.TryCreate(listenUrl ?? "http://localhost:5080", UriKind.Absolute, out var listen)
            || listen.Scheme is not ("http" or "https") || !IsOrigin(listen))
            throw new InvalidOperationException("监听地址必须是有效的 HTTP/HTTPS 地址，不能带路径或凭据。");
        if (localDevelopment)
        {
            if (!listen.IsLoopback || !string.IsNullOrEmpty(publicUrl))
                throw new InvalidOperationException("本地开发必须使用环回地址，不能配置公网入口。");
            return new(true, listen, null);
        }
        if (string.IsNullOrEmpty(publicUrl))
        {
            if (listen.Scheme != "https")
                throw new InvalidOperationException("正式访问必须使用 HTTPS，反向代理部署需配置 public-url。");
            return new(false, listen, null);
        }
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var external)
            || external.Scheme != "https" || !IsOrigin(external)
            || !listen.IsLoopback || listen.Scheme != "http")
            throw new InvalidOperationException("反向代理公网入口必须为 HTTPS，后端必须监听本机 HTTP 环回地址。");
        return new(false, listen, external);
    }

    public ForwardedHeadersOptions Forwarding()
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
            RequireHeaderSymmetry = true
        };
        // 仅信任同机代理的一跳；不接受任意上游声明的客户端地址和 HTTPS 状态。
        options.KnownIPNetworks.Clear(); options.KnownProxies.Clear();
        options.KnownProxies.Add(IPAddress.Loopback); options.KnownProxies.Add(IPAddress.IPv6Loopback);
        return options;
    }

    public bool Allows(HttpContext context)
    {
        if (!BehindProxy) return true;
        if (context.Request.Path == "/health/live"
            && context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote)
            && Uri.TryCreate("http://" + context.Request.Host, UriKind.Absolute, out var health) && health.IsLoopback)
            return true;
        return context.Request.IsHttps
            && string.Equals(context.Request.Host.Host, PublicUrl!.Host, StringComparison.OrdinalIgnoreCase)
            && (context.Request.Host.Port ?? 443) == PublicUrl.Port;
    }

    private static bool IsOrigin(Uri uri) => uri.AbsolutePath == "/" && uri.Query.Length == 0
        && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0 && uri.Port > 0;
}
