using Howie.Monitor.Contracts;

namespace Howie.Monitor.Server;

public static class AgentUpdateEndpoints
{
    public static void Map(WebApplication app, Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> csrf)
    {
        var viewing = app.MapGroup("/api/agent-updates").RequireAuthorization();
        viewing.MapGet("/status", (string computerId, TelemetryStore telemetry, SetupPackage package, TerminalBroker terminals) =>
        {
            if (!ValidId(computerId)) return Results.BadRequest();
            var status = telemetry.UpdateStatus(computerId, package.Release(), DateTimeOffset.UtcNow);
            if (status is null) return Results.NotFound();
            if (status.CanUpdate && terminals.HasActiveSession(computerId))
                status = status with { CanUpdate = false, Reason = "请先结束这台电脑的远程终端会话。" };
            return Results.Ok(status);
        });
        viewing.MapPost("", (AgentUpdateRequest request, CredentialStore credentials, TelemetryStore telemetry, SetupPackage package, TerminalBroker terminals) =>
        {
            if (!ValidId(request.ComputerId) || !Guid.TryParse(request.ExpectedSessionId, out _) || !AgentRelease.Hash(request.Sha256))
                return Results.BadRequest(new { error = "升级目标或版本不正确。" });
            if (!credentials.VerifyPassword(request.Password)) return Results.Json(new { error = "面板密码不正确。" }, statusCode: 403);
            IResult response = Results.NotFound(new { error = "电脑不存在或正在移除。" });
            var release = package.Release();
            telemetry.WithComputer(request.ComputerId, _ =>
            {
                if (terminals.HasActiveSession(request.ComputerId)) { response = Results.Conflict(new { error = "请先结束这台电脑的远程终端会话。" }); return; }
                var result = telemetry.CreateUpdate(request, release, DateTimeOffset.UtcNow);
                response = result.Operation is null ? Results.Conflict(new { error = result.Error }) : Results.Ok(result.Operation);
            });
            return response;
        }).AddEndpointFilter(csrf).RequireRateLimiting("login");
        app.MapPost("/api/ingest/updates/poll", (AgentUpdatePoll poll, HttpContext context, TelemetryStore telemetry) =>
        {
            if (!Guid.TryParse(poll.SessionId, out _)) return Results.BadRequest();
            var result = telemetry.PollUpdate(context.Request.Headers.Authorization, poll.SessionId, DateTimeOffset.UtcNow);
            return result.Authorized ? Results.Ok(new AgentUpdatePollResponse(result.Ticket)) : Results.Unauthorized();
        });
        app.MapPost("/api/ingest/updates/report", (AgentUpdateReport report, HttpContext context, TelemetryStore telemetry) =>
        {
            var result = telemetry.ReportUpdate(context.Request.Headers.Authorization, report, DateTimeOffset.UtcNow);
            return !result.Authorized ? Results.Unauthorized() : result.Accepted ? Results.NoContent()
                : Results.Conflict(new { error = "升级状态或新版本上报尚未确认。" });
        });
        app.MapGet("/api/ingest/updates/{id}/health", (string id, bool rollback, HttpContext context, TelemetryStore telemetry) =>
            telemetry.UpdateHealth(context.Request.Headers.Authorization, id, rollback, DateTimeOffset.UtcNow) is { } health
                ? Results.Ok(health) : Results.Unauthorized());
    }
    private static bool ValidId(string? id) => id is { Length: > 0 and <= 200 } && !id.Any(char.IsControl) && !string.IsNullOrWhiteSpace(id);
}
