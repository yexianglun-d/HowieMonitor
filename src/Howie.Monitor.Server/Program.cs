using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Howie.Monitor.Contracts;
using Howie.Monitor.Server;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var builder = WebApplication.CreateBuilder(args);
bool localDevelopment = builder.Configuration.GetValue<bool>("local-development");
string dataDirectory = Path.GetFullPath(builder.Configuration["data-dir"] ?? ".local/server");
if (builder.Configuration.GetValue<bool>("initialize-admin"))
{ Environment.ExitCode = AdminInitialization.Run(dataDirectory); return; }
var hosting = ServerHosting.Create(localDevelopment, builder.Configuration["server-url"], builder.Configuration["public-url"]);
builder.WebHost.UseUrls(hosting.ListenUrl.AbsoluteUri);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddSingleton(new CredentialStore(dataDirectory));
builder.Services.AddSingleton(new TelemetryStore(dataDirectory));
builder.Services.AddHostedService<HistoryMaintenanceWorker>();
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
builder.Services.AddSingleton(provider => new NotificationStore(dataDirectory, provider.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()));
builder.Services.AddSingleton<ServerChanSender>();
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddSingleton(new SetupPackage(builder.Configuration["setup-package"] ?? Path.Combine(dataDirectory, "downloads", "HowieMonitorSetup.exe")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "howie.session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = localDevelopment ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
    options.Events.OnValidatePrincipal = async context =>
    {
        if (!context.HttpContext.RequestServices.GetRequiredService<CredentialStore>()
            .ValidSession(context.Principal?.FindFirstValue("stamp")))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync();
        }
    };
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = localDevelopment ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("enrollment", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 6, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.Services.GetRequiredService<TelemetryStore>().InitializeEnrollment(app.Services.GetRequiredService<CredentialStore>().Device,
    app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Howie.Monitor.DeviceEnrollment.v1"));
if (hosting.BehindProxy) app.UseForwardedHeaders(hosting.Forwarding());
app.UseExceptionHandler(handler => handler.Run(async context =>
    await Results.Problem("服务处理请求失败，请检查本地运行日志。", statusCode: 500).ExecuteAsync(context)));
app.Use(async (context, next) =>
{
    if (!hosting.Allows(context)) { context.Response.StatusCode = 400; return; }
    if (localDevelopment && context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote))
    { context.Response.StatusCode = 403; return; }
    if (context.Request.ContentLength > 1024 * 1024) { context.Response.StatusCode = 413; return; }
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/auth/session", (HttpContext context, CredentialStore credentials) =>
    Results.Ok(new { authenticated = context.User.Identity?.IsAuthenticated == true, setupRequired = !credentials.IsSetup }));
app.MapGet("/api/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
    Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken }));
foreach (bool setup in new[] { true, false })
{
    app.MapPost(setup ? "/api/auth/setup" : "/api/auth/login", async (LoginRequest request, HttpContext context, CredentialStore credentials) =>
    {
        if (request.Password is null || request.Password.Length is < 1 or > 128) return Results.BadRequest(new { error = "密码格式不正确。" });
        if (setup && !localDevelopment) return Results.BadRequest(new { error = "请通过本地部署初始化管理员账号。" });
        string? stamp = credentials.Login(request.Password, setup);
        if (stamp is null) return Results.Json(new { error = setup ? "首次密码至少 12 位，且只能初始化一次。" : "密码不正确。" }, statusCode: 401);
        await context.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "owner"), new Claim("stamp", stamp)], CookieAuthenticationDefaults.AuthenticationScheme)));
        return Results.Ok(new { authenticated = true });
    }).RequireRateLimiting("login").AddEndpointFilter(Csrf);
}
app.MapPost("/api/auth/logout", async (HttpContext context, CredentialStore credentials) =>
{
    credentials.Logout(context.User.FindFirstValue("stamp")); await context.SignOutAsync(); return Results.NoContent();
}).RequireAuthorization().AddEndpointFilter(Csrf);

var ingest = app.MapGroup("/api/ingest").AddEndpointFilter(async (context, next) =>
{
    var store = context.HttpContext.RequestServices.GetRequiredService<TelemetryStore>();
    if (!store.AuthenticateIngest(context.HttpContext.Request.Headers.Authorization, DateTimeOffset.UtcNow)) return Results.Unauthorized();
    return await next(context);
});
ingest.MapPost("/device", (DeviceRegistration device, HttpContext context, TelemetryStore store) =>
{
    var result = store.RegisterAuthenticated(context.Request.Headers.Authorization, device, DateTimeOffset.UtcNow);
    return !result.Authorized ? Results.Unauthorized() : result.Error is not null ? Results.BadRequest(new { error = result.Error }) : Results.NoContent();
});
ingest.MapPost("/heartbeat", (Heartbeat beat, HttpContext context, TelemetryStore store) =>
{
    var result = store.BeatAuthenticated(context.Request.Headers.Authorization, beat, DateTimeOffset.UtcNow);
    return !result.Authorized ? Results.Unauthorized() : result.Error is not null ? Results.BadRequest(new { error = result.Error }) : Results.NoContent();
});
ingest.MapPost("/samples", (IngestRequest request, HttpContext context, TelemetryStore store) =>
{
    var result = store.IngestAuthenticated(context.Request.Headers.Authorization, request, DateTimeOffset.UtcNow);
    return !result.Authorized ? Results.Unauthorized() : result.Error is not null ? Results.BadRequest(new { error = result.Error }) : Results.Ok(result.Acknowledgement);
});
ingest.MapGet("/status", (HttpContext context, TelemetryStore store) =>
{
    var status = store.EnrollmentStatus(context.Request.Headers.Authorization, DateTimeOffset.UtcNow);
    return status is null ? Results.Unauthorized() : Results.Ok(status);
});
ingest.MapPost("/activate", (EnrollmentActivation request, HttpContext context, TelemetryStore store) =>
{
    if (!Guid.TryParse(request.RequestId, out _)) return Results.BadRequest(new { error = "接入请求身份不正确。" });
    var result = store.ActivateEnrollment(context.Request.Headers.Authorization, request.RequestId, DateTimeOffset.UtcNow);
    return !result.Authorized ? Results.Unauthorized() : result.Error is not null ? Results.BadRequest(new { error = result.Error }) : Results.Ok(result.Progress);
});
app.MapPost("/api/enrollment/claim", (EnrollmentClaim request, TelemetryStore store) =>
{
    var result = store.ClaimEnrollment(request, DateTimeOffset.UtcNow);
    return result.Error is not null ? Results.BadRequest(new { error = result.Error }) : Results.Ok(result.Credential);
}).RequireRateLimiting("enrollment");
var viewing = app.MapGroup("/api").RequireAuthorization();
viewing.MapGet("/enrollment", (TelemetryStore store) => Results.Ok(store.EnrollmentOverview(DateTimeOffset.UtcNow)));
viewing.MapGet("/setup", (SetupPackage package) => Results.Ok(package.Info()));
viewing.MapGet("/setup/download", (SetupPackage package) => package.Info().Available
    ? Results.File(package.Path, "application/octet-stream", fileDownloadName: "HowieMonitorSetup.exe", enableRangeProcessing: true)
    : Results.NotFound(new { error = "安装包尚未发布，请稍后刷新。" }));
viewing.MapPost("/enrollment/code", (EnrollmentCodeRequest request, TelemetryStore store) =>
{
    var result = store.CreateEnrollmentCode(request, DateTimeOffset.UtcNow);
    return result.Error is not null ? Results.BadRequest(new { error = result.Error }) : Results.Ok(result.Code);
}).AddEndpointFilter(Csrf).RequireRateLimiting("enrollment");
viewing.MapDelete("/enrollment/code", (TelemetryStore store) => { store.RevokeEnrollment(); return Results.NoContent(); }).AddEndpointFilter(Csrf);
viewing.MapGet("/notifications", (NotificationStore store) => Results.Ok(store.View(DateTimeOffset.UtcNow)));
viewing.MapPut("/notifications", (NotificationUpdate update, NotificationStore store) =>
{
    string? error = store.Update(update);
    return error is null ? Results.Ok(store.View(DateTimeOffset.UtcNow)) : Results.BadRequest(new { error });
}).AddEndpointFilter(Csrf);
viewing.MapPost("/notifications/test", (NotificationStore store) =>
{
    string? error = store.QueueTest(DateTimeOffset.UtcNow, (hosting.PublicUrl ?? hosting.ListenUrl).AbsoluteUri);
    return error is null ? Results.Ok(store.View(DateTimeOffset.UtcNow)) : Results.BadRequest(new { error });
}).AddEndpointFilter(Csrf);
viewing.MapGet("/computers", (TelemetryStore store) => Results.Ok(store.Computers(DateTimeOffset.UtcNow)));
viewing.MapGet("/device", (string? computerId, TelemetryStore store) =>
{
    if (!ValidComputerId(computerId)) return Results.BadRequest(new { error = "电脑标识不正确。" });
    var device = store.Device(computerId);
    return computerId is not null && device is null ? Results.NotFound(new { error = "电脑尚未接入。" }) : Results.Ok(device);
});
viewing.MapGet("/metrics/latest", (string? computerId, TelemetryStore store) =>
{
    if (!ValidComputerId(computerId)) return Results.BadRequest(new { error = "电脑标识不正确。" });
    return computerId is not null && store.Device(computerId) is null ? Results.NotFound(new { error = "电脑尚未接入。" })
        : Results.Ok(store.Latest(DateTimeOffset.UtcNow, computerId));
});
viewing.MapGet("/processes", (string? computerId, TelemetryStore store) =>
{
    if (!ValidComputerId(computerId)) return Results.BadRequest(new { error = "电脑标识不正确。" });
    return computerId is not null && store.Device(computerId) is null ? Results.NotFound(new { error = "电脑尚未接入。" })
        : Results.Ok(store.Latest(DateTimeOffset.UtcNow, computerId).Sample?.Processes ?? []);
});
viewing.MapGet("/history", (string key, string deviceId, string? unit, int? minutes, string? computerId, TelemetryStore store) =>
{
    if (key.Length > 200 || deviceId.Length > 200 || unit?.Length > 20 || (minutes ?? 10) is < 1 or > 60 || !ValidComputerId(computerId))
        return Results.BadRequest(new { error = "近期曲线支持 1–60 分钟，指标与设备标识需有效。" });
    if (computerId is not null && store.Device(computerId) is null) return Results.NotFound(new { error = "电脑尚未接入。" });
    var now = DateTimeOffset.UtcNow;
    return Results.Ok(store.History(key, deviceId, now.AddMinutes(-(minutes ?? 10)), now, unit, computerId));
});
viewing.MapGet("/history/computers", (TelemetryStore store) => Results.Ok(store.HistoryComputers(DateTimeOffset.UtcNow)));
viewing.MapGet("/history/metrics", (string? computerId, TelemetryStore store) =>
{
    if (!ValidComputerId(computerId)) return Results.BadRequest(new { error = "电脑标识不正确。" });
    string? owner = store.ResolveHistoryComputer(computerId, DateTimeOffset.UtcNow);
    return owner is null ? Results.NotFound(new { error = "该电脑的历史不存在或已过保留期。" }) : Results.Ok(store.HistoryMetrics(owner));
});
viewing.MapGet("/history/series", (string key, string deviceId, string unit, int? hours, string? computerId, TelemetryStore store) =>
{
    if (string.IsNullOrWhiteSpace(key) || key.Length > 200 || string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 200
        || unit.Length is < 1 or > 20 || (hours ?? 24) is not (1 or 24 or 168) || !ValidComputerId(computerId))
        return Results.BadRequest(new { error = "请选择有效指标与单位，历史范围支持 1 小时、24 小时或七天。" });
    var now = DateTimeOffset.UtcNow; string? owner = store.ResolveHistoryComputer(computerId, now);
    return owner is null ? Results.NotFound(new { error = "该电脑的历史不存在或已过保留期。" })
        : Results.Ok(store.QueryHistory(owner, key, deviceId, unit, hours ?? 24, now));
});
viewing.MapGet("/history/processes", (string sessionId, long sequence, string? computerId, TelemetryStore store) =>
{
    if (!Guid.TryParse(sessionId, out _) || sequence < 0 || !ValidComputerId(computerId)) return Results.BadRequest(new { error = "历史采样身份不正确。" });
    string? owner = store.ResolveHistoryComputer(computerId, DateTimeOffset.UtcNow);
    if (owner is null) return Results.NotFound(new { error = "该电脑的历史不存在或已过保留期。" });
    var processes = store.HistoryProcesses(owner, new(sessionId, sequence));
    return processes is null ? Results.NotFound(new { error = "该采样的主要进程记录不存在或已过保留期。" }) : Results.Ok(processes);
});
app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api")) { context.Response.StatusCode = 404; return; }
    string path = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html");
    if (File.Exists(path)) await Results.File(path, "text/html").ExecuteAsync(context);
    else await Results.Problem("网页尚未构建，请先运行前端构建。", statusCode: 503).ExecuteAsync(context);
});
app.Run();

static bool ValidComputerId(string? value) => value is null || value.Length is > 0 and <= 200 && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

static async ValueTask<object?> Csrf(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
{
    try { await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext); }
    catch (AntiforgeryValidationException) { return Results.BadRequest(new { error = "请求验证失效，请刷新页面后重试。" }); }
    return await next(context);
}
public sealed record LoginRequest(string Password);
public partial class Program;
