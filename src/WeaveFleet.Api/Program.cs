// Dev workflow:
//
// Mode A — Integrated (backend serves built SPA):
//   Terminal 1: cd client && npm run build
//   Terminal 2: dotnet run --project src/WeaveFleet.Api
//   → http://localhost:5001 (API + SPA)
//
// Mode B — Split (frontend dev server + backend API):
//   Terminal 1: dotnet run --project src/WeaveFleet.Api
//   Terminal 2: cd client && npm run dev:split
//   → Frontend: http://localhost:3001 (hot reload)
//   → Backend:  http://localhost:5001 (API only)

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using WeaveFleet.Api.Endpoints;
using WeaveFleet.Api.Telemetry;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure;
using WeaveFleet.Infrastructure.Data;
using WeaveFleet.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

// Bind Fleet options
var fleetOptions = builder.Configuration
    .GetSection(FleetOptions.SectionName)
    .Get<FleetOptions>() ?? new FleetOptions();

// Configure services
builder.Services.Configure<FleetOptions>(
    builder.Configuration.GetSection(FleetOptions.SectionName));
builder.Services.AddFleetInfrastructure(fleetOptions);
builder.AddFleetTelemetry();
builder.Services.AddHealthChecks();

// ── Data Protection ───────────────────────────────────────────────────────────
var dataProtectionBuilder = builder.Services.AddDataProtection()
    .SetApplicationName("WeaveFleet");

if (!string.IsNullOrWhiteSpace(fleetOptions.DataProtection.KeyPath))
{
    dataProtectionBuilder.PersistKeysToFileSystem(
        new System.IO.DirectoryInfo(fleetOptions.DataProtection.KeyPath));
}

// ── CORS ─────────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (fleetOptions.Auth.Enabled && fleetOptions.Auth.AllowedOrigins.Length > 0)
        {
            // Cloud mode: restrict to explicitly configured origins, allow credentials for cookies
            policy.WithOrigins(fleetOptions.Auth.AllowedOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
        else if (builder.Environment.IsDevelopment())
        {
            // Allow localhost frontend dev servers during split-mode development
            policy.WithOrigins("http://localhost:3001", "https://localhost:3001")
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
        else
        {
            // Production local mode: same-origin only (SPA is served from the same host)
            policy.WithOrigins($"http://{fleetOptions.Host}:{fleetOptions.Port}")
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
    });
});

// ── Authentication / Authorization ───────────────────────────────────────────
builder.Services.AddHttpContextAccessor();

if (fleetOptions.Auth.Enabled)
{
    // Cloud mode: cookie auth (default) + OIDC challenge scheme
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.Cookie.Name = fleetOptions.Auth.CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(fleetOptions.Auth.CookieExpirationMinutes);
        options.SlidingExpiration = true;
        options.LoginPath = "/auth/login";

        // Return 401/403 for API and WebSocket requests instead of redirecting to IdP.
        // WebSocket handshakes cannot follow HTML login redirects, so redirects surface
        // in the browser as opaque connection failures rather than actionable auth errors.
        options.Events.OnRedirectToLogin = context =>
        {
            if (IsApiOrWebSocketRequest(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (IsApiOrWebSocketRequest(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    })
    .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
    {
        options.Authority = fleetOptions.Auth.Authority;
        options.ClientId = fleetOptions.Auth.ClientId;
        options.ClientSecret = fleetOptions.Auth.ClientSecret;
        options.CallbackPath = fleetOptions.Auth.CallbackPath;
        options.SignedOutCallbackPath = fleetOptions.Auth.SignedOutCallbackPath;
        options.ResponseType = "code";
        options.SaveTokens = false;  // no bearer tokens stored in browser
        options.GetClaimsFromUserInfoEndpoint = true;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
    });

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("FleetUser", policy =>
            policy.RequireAuthenticatedUser());
    });

    // Cloud mode: IUserContext reads from HTTP claims
    builder.Services.AddScoped<IUserContext, ClaimsUserContext>();
}
else
{
    // Local mode: stub authentication + passthrough IUserContext
    builder.Services.AddAuthentication();
    builder.Services.AddAuthorization();

    builder.Services.AddScoped<IUserContext, LocalUserContext>();
}

// ── Antiforgery ──────────────────────────────────────────────────────────────
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-Token";
    options.Cookie.Name = ".WeaveFleet.Antiforgery";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.HttpOnly = true;
});

var app = builder.Build();

if (fleetOptions.Auth.Enabled)
{
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    });
}

// Run database migrations at startup
var migrationRunner = app.Services.GetRequiredService<MigrationRunner>();
await migrationRunner.ApplyMigrationsAsync();

// Run analytics database migrations (only when analytics is enabled)
var analyticsMigrationRunner = app.Services.GetService<AnalyticsMigrationRunner>();
if (analyticsMigrationRunner is not null)
    await analyticsMigrationRunner.ApplyMigrationsAsync();

// Ensure scratch project exists in local mode only.
// In auth/cloud mode, scratch projects are user-scoped and created on demand.
if (!fleetOptions.Auth.Enabled)
{
    using var scope = app.Services.CreateScope();
    var projectService = scope.ServiceProvider.GetRequiredService<ProjectService>();
    await projectService.EnsureScratchProjectAsync();
}

// Recovery: mark all previously-running instances and non-terminal sessions as stopped
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // Orphan killing: kill any child processes from the previous run before marking them stopped
    var instanceRepo = scope.ServiceProvider.GetRequiredService<IInstanceRepository>();
    var runningInstances = await instanceRepo.GetRunningAsync();
    foreach (var instance in runningInstances)
    {
        if (instance.Pid is not { } pid) continue;
        try
        {
            var proc = System.Diagnostics.Process.GetProcessById(pid);
            // Guard against PID reuse: only kill if the process name looks like a known agent
            var procName = proc.ProcessName;
            if (procName.Contains("opencode", StringComparison.OrdinalIgnoreCase)
                || procName.Contains("claude", StringComparison.OrdinalIgnoreCase)
                || procName.Contains("node", StringComparison.OrdinalIgnoreCase))
            {
                StartupLog.OrphanKilling(logger, pid, procName);
                proc.Kill(entireProcessTree: true);
            }
            else
            {
                StartupLog.OrphanSkipped(logger, pid, procName);
            }
        }
        catch (ArgumentException)
        {
            // Process already exited — not an error
        }
        catch (Exception ex)
        {
            StartupLog.OrphanKillFailed(logger, pid, ex);
        }
    }

    var instanceService = scope.ServiceProvider.GetRequiredService<InstanceService>();
    var instanceCount = await instanceService.MarkAllStoppedAsync();
    var sessionCount = await instanceService.MarkAllNonTerminalSessionsStoppedAsync();
    if (instanceCount > 0 || sessionCount > 0)
        StartupLog.RecoveryComplete(logger, instanceCount, sessionCount);
}

// Middleware pipeline
app.UseCors();

app.Use(async (context, next) =>
{
    if (!fleetOptions.Auth.Enabled)
    {
        await next();
        return;
    }

    var requiresAntiforgery = context.Request.Path.StartsWithSegments("/api")
        || context.Request.Path.StartsWithSegments("/auth/logout");

    if (!requiresAntiforgery)
    {
        await next();
        return;
    }

    var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();

    if (HttpMethods.IsGet(context.Request.Method)
        || HttpMethods.IsHead(context.Request.Method)
        || HttpMethods.IsOptions(context.Request.Method)
        || HttpMethods.IsTrace(context.Request.Method))
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        if (!string.IsNullOrWhiteSpace(tokens.RequestToken))
        {
            context.Response.Cookies.Append(
                ".WeaveFleet.CSRF",
                tokens.RequestToken,
                new CookieOptions
                {
                    HttpOnly = false,
                    SameSite = SameSiteMode.Strict,
                    Secure = true,
                    Path = "/"
                });
        }

        await next();
        return;
    }

    try
    {
        await antiforgery.ValidateRequestAsync(context);
        await next();
    }
    catch (AntiforgeryValidationException)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "Invalid or missing CSRF token." });
    }
});

if (fleetOptions.Auth.Enabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

// WebSocket support (for /ws real-time events endpoint)
app.UseWebSockets();

// Health checks (registered before SPA fallback)
app.MapHealthChecks("/healthz");
app.MapHealthChecks("/readyz");
app.MapGet("/version", () => Results.Ok(new
{
    version = FleetInstrumentation.ServiceVersion,
    commit = FleetInstrumentation.ServiceCommit
}));
app.MapAuthEndpoints(fleetOptions);

// API endpoints (registered before SPA fallback)
app.MapFleetEndpoints();

// Static file serving (SPA)
app.UseDefaultFiles();   // Serves index.html for "/"
app.UseStaticFiles();    // Serves files from wwwroot/

// SPA fallback — any unmatched route serves index.html for client-side routing
app.MapFallbackToFile("index.html");

// Graceful shutdown: stop all tracked harness instances before the process exits.
// Resolve dependencies up front (the root IServiceProvider may already be disposed by the
// time ApplicationStopping fires — both are singletons, so capturing them is safe).
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var shutdownTracker = app.Services.GetRequiredService<InstanceTracker>();
var shutdownLogger = app.Services.GetRequiredService<ILogger<Program>>();
lifetime.ApplicationStopping.Register(() =>
{
    var instances = shutdownTracker.GetAll();
    if (instances.Count == 0) return;

    StartupLog.GracefulShutdownStarted(shutdownLogger, instances.Count);

    var tasks = instances.Values.Select(async session =>
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await session.StopAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StartupLog.GracefulShutdownInstanceFailed(shutdownLogger, session.InstanceId, ex);
        }
    });

    Task.WhenAll(tasks).GetAwaiter().GetResult();
    StartupLog.GracefulShutdownComplete(shutdownLogger, instances.Count);
});

await app.RunAsync();

static bool IsApiOrWebSocketRequest(PathString path)
    => path.StartsWithSegments("/api") || path.StartsWithSegments("/ws");

/// <summary>Logger message definitions for startup diagnostics.</summary>
internal static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Recovery: marked {Instances} instance(s) and {Sessions} session(s) as stopped.")]
    public static partial void RecoveryComplete(ILogger logger, int instances, int sessions);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Graceful shutdown: stopping {Count} tracked instance(s).")]
    public static partial void GracefulShutdownStarted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Graceful shutdown: failed to stop instance {InstanceId}.")]
    public static partial void GracefulShutdownInstanceFailed(ILogger logger, string instanceId, Exception ex);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Graceful shutdown: stopped {Count} instance(s).")]
    public static partial void GracefulShutdownComplete(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Startup orphan kill: killing pid {Pid} ({ProcessName}).")]
    public static partial void OrphanKilling(ILogger logger, int pid, string processName);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Startup orphan skip: pid {Pid} ({ProcessName}) does not match known agent names.")]
    public static partial void OrphanSkipped(ILogger logger, int pid, string processName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Startup orphan kill failed for pid {Pid}.")]
    public static partial void OrphanKillFailed(ILogger logger, int pid, Exception ex);
}

// Expose Program for WebApplicationFactory in E2E tests
public partial class Program { }
