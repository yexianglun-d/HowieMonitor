using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Agent;

public sealed class TerminalWorker(AgentState state, HttpClient client, ILogger<TerminalWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? lastFailure = null;
        Task? active = null; CancellationTokenSource? sessionLifetime = null; string? activeLogon = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (active?.IsCompleted == true)
                    { await active; active = null; sessionLifetime?.Dispose(); sessionLifetime = null; }
                    if (state.Registered)
                    {
                        var capability = Capability();
                        if (active is not null && (!capability.Available || capability.LogonId != activeLogon))
                        {
                            sessionLifetime!.Cancel(); await active; active = null;
                            sessionLifetime.Dispose(); sessionLifetime = null;
                        }
                        using var response = await client.PostAsJsonAsync("/api/ingest/terminal/poll", capability, ProtocolJson.Options, stoppingToken);
                        response.EnsureSuccessStatusCode(); lastFailure = null;
                        var poll = await response.Content.ReadFromJsonAsync<TerminalPollResponse>(ProtocolJson.Options, stoppingToken)
                            ?? throw new InvalidDataException("终端服务响应为空。");
                        var offer = poll.Offer;
                        if (offer is not null)
                        {
                            if (active is not null || !capability.Available || offer.ComputerId != state.Configuration.DeviceId
                                || offer.AgentSessionId != state.Registration.SessionId || !Guid.TryParseExact(offer.SessionId, "N", out _)
                                || !TerminalProtocol.ValidSize(offer.Columns, offer.Rows))
                                throw new InvalidDataException("终端会话归属或尺寸不正确。");
                            sessionLifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            activeLogon = capability.LogonId;
                            active = Run(offer, capability, sessionLifetime.Token);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    // 终端通道独立失败，停止当前命令会话，性能采集与离线补传仍继续运行。
                    sessionLifetime?.Cancel();
                    string failure = error.GetType().Name;
                    if (lastFailure != failure) logger.LogWarning("终端通道不可用：{Error}", failure);
                    lastFailure = failure;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            if (sessionLifetime is not null) await sessionLifetime.CancelAsync();
            if (active is not null) await active;
            sessionLifetime?.Dispose();
        }
    }

    private TerminalCapability Capability()
    {
        var access = state.Configuration.Terminal;
        if (access is not { Enabled: true }) return new(state.Registration.SessionId, false, false, null, null, "尚未在本机安装向导中开启管理员终端。");
        try
        {
            using var identity = AdminTerminalIdentity.Acquire(access.UserSid);
            return new(state.Registration.SessionId, true, true, identity.Account, identity.UserSid, null, identity.LogonId);
        }
        catch (Exception error)
        {
            return new(state.Registration.SessionId, true, false, null, null,
                error is TerminalAccessException ? error.Message : "管理员身份暂不可读取，请在本机检查授权。");
        }
    }

    private async Task Run(TerminalOffer offer, TerminalCapability expected, CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + state.Configuration.Token);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10); socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
        var uri = new UriBuilder(state.Configuration.ServerUrl)
        { Scheme = new Uri(state.Configuration.ServerUrl).Scheme == "https" ? "wss" : "ws", Path = "/api/ingest/terminal/sessions/" + offer.SessionId + "/socket" }.Uri;
        WindowsAdminTerminal? terminal = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? input = null, output = null, exited = null;
        try
        {
            using (var connection = CancellationTokenSource.CreateLinkedTokenSource(token))
            { connection.CancelAfter(TimeSpan.FromSeconds(10)); await socket.ConnectAsync(uri, connection.Token); }
            token.ThrowIfCancellationRequested();
            terminal = await WindowsAdminTerminal.Start(expected.UserSid!, offer.Columns, offer.Rows);
            if (terminal.Identity.LogonId != expected.LogonId) throw new TerminalAccessException("管理员登录会话已变更。");
            await TerminalProtocol.Send(socket, new("ready", Account: terminal.Identity.Account, UserSid: terminal.Identity.UserSid,
                Administrator: true, LogonId: terminal.Identity.LogonId), lifetime.Token);
            async Task Input()
            {
                while (true)
                {
                    var frame = await TerminalProtocol.Receive(socket, lifetime.Token);
                    if (frame is null || frame.Type == "close") return;
                    if (!TerminalProtocol.ValidBrowserFrame(frame)) throw new InvalidDataException("终端输入不正确。");
                    if (frame.Type == "input") await terminal.Write(frame.Data!, lifetime.Token);
                    else if (frame.Type == "resize") terminal.Resize(frame.Columns, frame.Rows);
                }
            }
            async Task Output()
            {
                var buffer = new byte[4096]; var characters = new char[8192]; var decoder = Encoding.UTF8.GetDecoder();
                while (true)
                {
                    int count = await terminal.Read(buffer, lifetime.Token);
                    if (count == 0) return;
                    int decoded = decoder.GetChars(buffer, 0, count, characters, 0, false);
                    if (decoded > 0) await TerminalProtocol.Send(socket, new("output", new string(characters, 0, decoded)), lifetime.Token);
                }
            }
            input = Input(); output = Output(); exited = terminal.WaitForExit(lifetime.Token);
            await await Task.WhenAny(input, output, exited);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            await lifetime.CancelAsync();
            if (output is not null) try { await output; } catch (Exception) { }
            string reason = error is TerminalAccessException ? error.Message : "管理员终端启动或连接失败。";
            logger.LogWarning("终端会话停止：{SessionId}，{Error}", offer.SessionId, error.GetType().Name);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (socket.State == WebSocketState.Open) await TerminalProtocol.Send(socket, new("error", reason), timeout.Token);
            }
            catch (Exception sendError) when (sendError is WebSocketException or OperationCanceledException) { }
        }
        finally
        {
            await lifetime.CancelAsync();
            foreach (var task in new[] { input, output, exited })
                if (task is not null) try { await task; } catch (Exception) { }
            try { if (terminal is not null) await terminal.DisposeAsync(); }
            catch (Exception error) { logger.LogWarning("终端进程清理失败：{SessionId}，{Error}", offer.SessionId, error.GetType().Name); }
            finally { await TerminalProtocol.Close(socket, "目标电脑终端已结束"); }
        }
    }
}
