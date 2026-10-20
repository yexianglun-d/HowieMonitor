using System.Net.WebSockets;
using System.Security.Claims;
using System.Threading.Channels;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Server;

public static class TerminalEndpoints
{
    public static void Map(WebApplication app, ServerHosting hosting,
        Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> csrf)
    {
        var viewing = app.MapGroup("/api/terminal").RequireAuthorization();
        viewing.MapGet("/status", (string? computerId, TelemetryStore telemetry, TerminalBroker broker) =>
            !ValidComputer(computerId) ? Results.BadRequest(new { error = "请选择目标电脑。" })
                : telemetry.Device(computerId) is null ? Results.NotFound(new { error = "目标电脑不存在。" })
                : Results.Ok(broker.Status(computerId!, DateTimeOffset.UtcNow)));
        viewing.MapPost("/sessions", (TerminalCreateRequest request, HttpContext context, CredentialStore credentials, TelemetryStore telemetry, TerminalBroker broker) =>
        {
            if (!ValidComputer(request.ComputerId) || !TerminalProtocol.ValidSize(request.Columns, request.Rows))
                return Results.BadRequest(new { error = "目标电脑或终端尺寸不正确。" });
            if (!credentials.VerifyPassword(request.Password)) return Results.Json(new { error = "面板密码不正确。" }, statusCode: 403);
            if (telemetry.Device(request.ComputerId) is null) return Results.NotFound(new { error = "目标电脑不存在。" });
            var result = broker.Create(request.ComputerId, context.User.FindFirstValue("stamp")!, request.Columns, request.Rows, DateTimeOffset.UtcNow);
            return result.Session is null ? Results.Conflict(new { error = result.Error })
                : Results.Ok(new TerminalSessionCreated(result.Session.Id, result.Session.ConnectBefore));
        }).AddEndpointFilter(csrf).RequireRateLimiting("login");
        viewing.MapDelete("/sessions/{id}", (string id, string? computerId, HttpContext context, TerminalBroker broker) =>
            !ValidComputer(computerId) ? Results.BadRequest(new { error = "请选择目标电脑。" })
                : broker.Close(id, computerId!, context.User.FindFirstValue("stamp")!) ? Results.NoContent()
                : Results.NotFound(new { error = "会话不存在或不属于当前登录。" })).AddEndpointFilter(csrf);
        viewing.MapGet("/sessions/{id}/socket", async (string id, string? computerId, HttpContext context, TerminalBroker broker) =>
        {
            if (!ValidComputer(computerId) || !SameOrigin(context, hosting)) { context.Response.StatusCode = 403; return; }
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            var session = broker.ClaimBrowser(id, computerId!, context.User.FindFirstValue("stamp")!, DateTimeOffset.UtcNow);
            if (session is null) { context.Response.StatusCode = 404; return; }
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await Run(socket, session, false, broker, context.RequestAborted);
            }
            finally { broker.Finish(session, "网页终端已断开"); }
        });

        app.MapPost("/api/ingest/terminal/poll", (TerminalCapability capability, HttpContext context, TelemetryStore telemetry, TerminalBroker broker) =>
        {
            var actor = telemetry.AuthorizeTerminal(context.Request.Headers.Authorization, DateTimeOffset.UtcNow);
            if (actor is null) return Results.Unauthorized();
            if (!ValidCapability(capability) || actor.Value.AgentSessionId != capability.AgentSessionId)
                return Results.BadRequest(new { error = "终端采集端身份不正确。" });
            return Results.Ok(new TerminalPollResponse(broker.Poll(actor.Value.DeviceId, capability, DateTimeOffset.UtcNow)));
        });
        app.MapGet("/api/ingest/terminal/sessions/{id}/socket", async (string id, HttpContext context, TelemetryStore telemetry, TerminalBroker broker) =>
        {
            var actor = telemetry.AuthorizeTerminal(context.Request.Headers.Authorization, DateTimeOffset.UtcNow);
            if (actor is null) { context.Response.StatusCode = 401; return; }
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            var session = broker.ClaimAgent(id, actor.Value.DeviceId, actor.Value.AgentSessionId, DateTimeOffset.UtcNow);
            if (session is null) { context.Response.StatusCode = 404; return; }
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await Run(socket, session, true, broker, context.RequestAborted);
            }
            finally { broker.Finish(session, "目标电脑终端已断开"); }
        });
    }

    public static bool ValidCapability(TerminalCapability capability) => Guid.TryParse(capability.AgentSessionId, out _)
        && capability.Account?.Length is not > 160 && capability.Reason?.Length is not > 256
        && (!capability.Available || capability.Enabled && TerminalProtocol.ValidUser(capability.UserSid) && !string.IsNullOrWhiteSpace(capability.Account)
            && capability.LogonId is { Length: > 0 and <= 64 });
    private static bool ValidComputer(string? id) => id is { Length: > 0 and <= 200 } && !string.IsNullOrWhiteSpace(id) && !id.Any(char.IsControl);
    public static bool SameOrigin(HttpContext context, ServerHosting hosting)
    {
        if (!Uri.TryCreate(context.Request.Headers.Origin.ToString(), UriKind.Absolute, out var origin)
            || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0) return false;
        Uri expected = hosting.PublicUrl ?? new Uri(context.Request.Scheme + "://" + context.Request.Host);
        return origin.Scheme == expected.Scheme && origin.Port == expected.Port && string.Equals(origin.Host, expected.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task Run(WebSocket socket, TerminalSession session, bool agent, TerminalBroker broker, CancellationToken requestToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(requestToken, session.Lifetime.Token);
        var token = lifetime.Token;
        async Task Read()
        {
            while (!token.IsCancellationRequested)
            {
                var frame = await TerminalProtocol.Receive(socket, requestToken);
                if (frame is null || frame.Type == "close") return;
                if (agent)
                {
                    if (frame.Type == "error")
                    {
                        broker.Finish(session, frame.Data is { Length: > 0 and <= 160 } ? frame.Data : "终端启动失败"); return;
                    }
                    if (!session.Ready)
                    {
                        if (frame.Type != "ready" || !frame.Administrator || frame.UserSid != session.UserSid || frame.Account != session.Account || frame.LogonId != session.LogonId)
                            throw new InvalidDataException("管理员执行身份不匹配。");
                        session.Ready = true;
                    }
                    else if (frame.Type != "output" || frame.Data is not { Length: > 0 and <= 16384 })
                        throw new InvalidDataException("终端输出不正确。");
                    if (frame.Type == "output") { await session.ReserveOutput(token); session.Touch(DateTimeOffset.UtcNow); }
                    await session.Browser.Writer.WriteAsync(frame, token);
                }
                else
                {
                    // ready 必须来自目标电脑；页面不能提前排队输入，更不能伪造执行身份。
                    if (!session.Ready || !TerminalProtocol.ValidBrowserFrame(frame)) throw new InvalidDataException("终端输入不正确。");
                    if (frame.Type == "ack")
                    {
                        if (!session.AcknowledgeOutput()) throw new InvalidDataException("终端输出确认不正确。");
                        continue;
                    }
                    if (frame.Type == "input") session.Touch(DateTimeOffset.UtcNow);
                    await session.Agent.Writer.WriteAsync(frame, token);
                }
            }
        }
        async Task Write()
        {
            var channel = agent ? session.Agent : session.Browser;
            await foreach (var frame in channel.Reader.ReadAllAsync(token))
            {
                using var send = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
                send.CancelAfter(TimeSpan.FromSeconds(2));
                await TerminalProtocol.Send(socket, frame, send.Token);
            }
        }
        Task? read = null, write = null;
        try
        {
            read = Read(); write = Write();
            await await Task.WhenAny(read, write);
        }
        catch (InvalidDataException) { broker.Finish(session, "终端消息或执行身份校验失败"); }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ChannelClosedException or IOException) { }
        finally
        {
            broker.Finish(session, agent ? "目标电脑终端已断开" : "网页终端已断开");
            await lifetime.CancelAsync();
            // 取消 ReceiveAsync 会直接中止底层 WebSocket；先结束写入并发送关闭原因，再释放读取。
            if (write is not null)
                try { await write; }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException or ChannelClosedException or IOException) { }
            await TerminalProtocol.Close(socket, session.EndReason, read);
            if (read is not null)
                try { await read; }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException or ChannelClosedException or IOException or InvalidDataException) { }
        }
    }
}
