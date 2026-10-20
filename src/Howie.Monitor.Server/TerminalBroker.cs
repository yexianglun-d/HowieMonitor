using System.Threading.Channels;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Server;

public sealed class TerminalSession
{
    private readonly string _owner;
    private int _ended;
    private int _pendingOutputs;
    private readonly SemaphoreSlim _outputSlots = new(32, 32);
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string ComputerId { get; }
    public string AgentSessionId { get; }
    public string Account { get; }
    public string UserSid { get; }
    public string LogonId { get; }
    public int Columns { get; }
    public int Rows { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastActivity { get; private set; }
    public DateTimeOffset ConnectBefore => CreatedAt.AddSeconds(20);
    public bool BrowserClaimed { get; internal set; }
    public bool Offered { get; internal set; }
    public bool AgentClaimed { get; internal set; }
    private volatile bool _ready;
    public bool Ready { get => _ready; internal set => _ready = value; }
    public bool Closed => Volatile.Read(ref _ended) != 0;
    public string EndReason { get; private set; } = "会话已结束";
    public CancellationTokenSource Lifetime { get; } = new();
    public Channel<TerminalFrame> Browser { get; } = Channel.CreateBounded<TerminalFrame>(64);
    public Channel<TerminalFrame> Agent { get; } = Channel.CreateBounded<TerminalFrame>(64);

    public TerminalSession(string computerId, string owner, TerminalCapability capability, int columns, int rows, DateTimeOffset now)
    {
        ComputerId = computerId; _owner = owner; AgentSessionId = capability.AgentSessionId;
        Account = capability.Account!; UserSid = capability.UserSid!; LogonId = capability.LogonId!; Columns = columns; Rows = rows;
        CreatedAt = LastActivity = now;
    }
    public bool OwnedBy(string owner) => _owner == owner;
    public bool IsValidOwner(Func<string?, bool> validate) => validate(_owner);
    public async Task ReserveOutput(CancellationToken token) { await _outputSlots.WaitAsync(token); Interlocked.Increment(ref _pendingOutputs); }
    public bool AcknowledgeOutput()
    {
        lock (_outputSlots)
        {
            if (Volatile.Read(ref _pendingOutputs) == 0) return false;
            Interlocked.Decrement(ref _pendingOutputs); _outputSlots.Release(); return true;
        }
    }
    public void Touch(DateTimeOffset now) { lock (this) LastActivity = now; }
    public bool Idle(DateTimeOffset now) { lock (this) return now - LastActivity > TimeSpan.FromMinutes(5); }
    public void End(string reason)
    {
        if (Interlocked.CompareExchange(ref _ended, 1, 0) != 0) return;
        EndReason = reason; Browser.Writer.TryComplete(); Agent.Writer.TryComplete(); Lifetime.Cancel();
    }
}

public sealed class TerminalBroker(ILogger<TerminalBroker> logger)
{
    private sealed record Presence(TerminalCapability Capability, DateTimeOffset SeenAt);
    private readonly object _gate = new();
    private readonly Dictionary<string, Presence> _computers = [];
    private readonly Dictionary<string, TerminalSession> _sessions = [];
    private static bool Fresh(Presence? presence, DateTimeOffset now) => presence is not null && now - presence.SeenAt <= TimeSpan.FromSeconds(15);
    private void End(TerminalSession session, string reason)
    {
        if (session.Closed) return;
        session.End(reason);
        logger.LogInformation("管理员终端结束：电脑 {ComputerId}，会话 {SessionId}，账号 {Account}，原因 {Reason}", session.ComputerId, session.Id, session.Account, reason);
    }
    public void Finish(TerminalSession session, string reason) { lock (_gate) End(session, reason); }

    public TerminalStatus Status(string computerId, DateTimeOffset now)
    {
        lock (_gate)
        {
            _computers.TryGetValue(computerId, out var presence);
            bool connected = Fresh(presence, now);
            return new(connected, connected && presence!.Capability.Available, connected ? presence!.Capability.Account : null,
                connected ? presence!.Capability.Reason : "终端通道未连接，请更新采集端并在本机开启管理员终端。",
                _sessions.Values.FirstOrDefault(session => session.ComputerId == computerId && !session.Closed)?.Id);
        }
    }

    public TerminalOffer? Poll(string computerId, TerminalCapability capability, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var session in _sessions.Values.Where(session => session.ComputerId == computerId && !session.Closed))
                if (!capability.Available || session.AgentSessionId != capability.AgentSessionId || session.UserSid != capability.UserSid || session.LogonId != capability.LogonId)
                    End(session, "管理员登录状态或本机授权已变更");
            _computers[computerId] = new(capability, now);
            var pending = _sessions.Values.FirstOrDefault(session => session.ComputerId == computerId && !session.Closed
                && session.BrowserClaimed && !session.Offered && now <= session.ConnectBefore);
            if (pending is null) return null;
            pending.Offered = true;
            return new(pending.Id, pending.ComputerId, pending.AgentSessionId, pending.Columns, pending.Rows);
        }
    }

    public (TerminalSession? Session, string? Error) Create(string computerId, string owner, int columns, int rows, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (string id in _sessions.Where(pair => pair.Value.Closed).Select(pair => pair.Key).ToArray()) _sessions.Remove(id);
            if (!TerminalProtocol.ValidSize(columns, rows)) return (null, "终端尺寸不正确。");
            if (_sessions.Count >= 32) return (null, "终端会话数量已达上限。");
            if (_sessions.Values.Any(session => session.ComputerId == computerId && !session.Closed)) return (null, "这台电脑已有终端会话，请先结束原会话。");
            if (!_computers.TryGetValue(computerId, out var presence) || !Fresh(presence, now) || !presence.Capability.Available)
                return (null, "这台电脑的管理员终端暂不可用。");
            if (!TerminalEndpoints.ValidCapability(presence.Capability))
                return (null, "管理员执行身份尚未确认。");
            var session = new TerminalSession(computerId, owner, presence.Capability, columns, rows, now);
            _sessions.Add(session.Id, session);
            logger.LogInformation("管理员终端创建：电脑 {ComputerId}，会话 {SessionId}，账号 {Account}", computerId, session.Id, session.Account);
            return (session, null);
        }
    }

    public TerminalSession? ClaimBrowser(string id, string computerId, string owner, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var session) || session.Closed || session.ComputerId != computerId
                || !session.OwnedBy(owner) || session.BrowserClaimed || now > session.ConnectBefore) return null;
            session.BrowserClaimed = true; return session;
        }
    }
    public TerminalSession? ClaimAgent(string id, string computerId, string agentSessionId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var session) || session.Closed || !session.BrowserClaimed || !session.Offered
                || session.AgentClaimed || session.ComputerId != computerId || session.AgentSessionId != agentSessionId || now > session.ConnectBefore) return null;
            session.AgentClaimed = true; return session;
        }
    }
    public bool Close(string id, string computerId, string owner)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var session) || session.ComputerId != computerId || !session.OwnedBy(owner)) return false;
            End(session, "已主动结束会话"); return true;
        }
    }
    public void Sweep(DateTimeOffset now, Func<string, bool> validSession, Func<string, string?> currentAgentSession)
    {
        lock (_gate)
        {
            foreach (var session in _sessions.Values.Where(session => !session.Closed))
            {
                _computers.TryGetValue(session.ComputerId, out var presence);
                if (!Fresh(presence, now)) End(session, "目标电脑的终端连接已中断");
                else if (!validSession(session.Id)) End(session, "网页登录已失效");
                else if (currentAgentSession(session.ComputerId) != session.AgentSessionId) End(session, "采集端身份已变更");
                else if (!session.Ready && now > session.ConnectBefore) End(session, "建立终端连接超时");
                else if (now - session.CreatedAt > TimeSpan.FromHours(1) || session.Idle(now)) End(session, "终端会话已超时");
            }
            foreach (string id in _sessions.Where(pair => pair.Value.Closed && now - pair.Value.CreatedAt > TimeSpan.FromMinutes(2)).Select(pair => pair.Key).ToArray())
                _sessions.Remove(id);
            foreach (string id in _computers.Where(pair => now - pair.Value.SeenAt > TimeSpan.FromMinutes(2)).Select(pair => pair.Key).ToArray()) _computers.Remove(id);
        }
    }
    public bool ValidOwner(string id, CredentialStore credentials)
    {
        lock (_gate) return _sessions.TryGetValue(id, out var session) && session.IsValidOwner(credentials.ValidSession);
    }
    public void Stop() { lock (_gate) foreach (var session in _sessions.Values) End(session, "终端服务已停止"); }
}

public sealed class TerminalMaintenanceWorker(TerminalBroker broker, CredentialStore credentials, TelemetryStore telemetry) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                broker.Sweep(DateTimeOffset.UtcNow, id => broker.ValidOwner(id, credentials), id => telemetry.Device(id)?.SessionId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { broker.Stop(); }
    }
}
