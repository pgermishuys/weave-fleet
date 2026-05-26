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

using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using WeaveFleet.Api;
using WeaveFleet.Api.Auth;
using WeaveFleet.Api.Endpoints;
using WeaveFleet.Api.Telemetry;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure;
using WeaveFleet.Infrastructure.Data;
using WeaveFleet.Infrastructure.Services;

// Suppress ILLink IL2026 for the top-level entry point: config binding uses simple POCOs;
// telemetry and plugin endpoint registration are safe at runtime.
[assembly: System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
    Scope = "member",
    Target = "M:Program.<<Main>$>d__0.MoveNext()",
    Justification = "Top-level program uses simple POCOs for config binding and telemetry; safe at runtime.")]

// ── CLI argument overrides ────────────────────────────────────────────────────
// Map friendly --host / --port flags to the Fleet configuration section so users
// can run:  fleet --host 0.0.0.0 --port 5001
var cliOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
string? harnessMode = Environment.GetEnvironmentVariable("FLEET_HARNESS");
var importLegacySessions = false;
string? legacyImportSourcePath = null;
string? cliArgumentError = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "--host" && i + 1 < args.Length)
        cliOverrides[$"{FleetOptions.SectionName}:Host"] = args[++i];
    else if (args[i] is "--port" && i + 1 < args.Length)
        cliOverrides[$"{FleetOptions.SectionName}:Port"] = args[++i];
    else if (args[i] is "--harness" && i + 1 < args.Length)
        harnessMode = args[++i];
    else if (args[i].StartsWith("--harness=", StringComparison.Ordinal))
        harnessMode = args[i]["--harness=".Length..];
    else if (args[i] is "--transport" && i + 1 < args.Length)
        cliOverrides[$"{FleetOptions.SectionName}:EventBus:Transport"] = args[++i];
    else if (args[i] is "--import-legacy-sessions")
        importLegacySessions = true;
    else if (args[i] is "--source")
    {
        if (i + 1 < args.Length)
            legacyImportSourcePath = args[++i];
        else
            cliArgumentError = "Missing value for --source.";
    }
}

if (cliArgumentError is not null)
{
    Console.Error.WriteLine(cliArgumentError);
    Environment.Exit(1);
}

var builder = WebApplication.CreateBuilder(args);

if (cliOverrides.Count > 0)
    builder.Configuration.AddInMemoryCollection(cliOverrides!);

// Bind Fleet options
#pragma warning disable IL2026 // FleetOptions is a simple POCO with primitive properties; safe at runtime
var fleetOptions = builder.Configuration
    .GetSection(FleetOptions.SectionName)
    .Get<FleetOptions>() ?? new FleetOptions();

// Configure services
builder.Services.Configure<FleetOptions>(
    builder.Configuration.GetSection(FleetOptions.SectionName));
if (!builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddLegacySessionImportStartupService();
builder.Services.AddFleetInfrastructure(fleetOptions);

// ── Harness mode override ─────────────────────────────────────────────────────
// --harness=test (or FLEET_HARNESS=test) swaps the production harnesses
// (OpenCode, ClaudeCode) for the in-process mock harness used by the
// beta-tester rig. Opt-in only; emits a loud warning at startup.
if (string.Equals(harnessMode, "test", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("=================================================================");
    Console.WriteLine("  FLEET RUNNING IN TEST HARNESS MODE — no real model API calls.");
    Console.WriteLine("  All harness traffic is served by WeaveFleet.TestHarness.");
    Console.WriteLine("=================================================================");

    // Drop production IHarness / IHarnessRuntime registrations.
    var harnessDescriptors = builder.Services
        .Where(d => d.ServiceType == typeof(WeaveFleet.Application.Harnesses.IHarness)
                 || d.ServiceType == typeof(WeaveFleet.Application.Harnesses.IHarnessRuntime))
        .ToList();
    foreach (var d in harnessDescriptors)
        builder.Services.Remove(d);

    // Singleton instances so test-driven runs can hold a stable reference if needed.
    var testHarness = new WeaveFleet.TestHarness.TestHarness();
    var testHarnessRuntime = new WeaveFleet.TestHarness.TestHarnessRuntime();

    builder.Services.AddSingleton<WeaveFleet.Application.Harnesses.IHarness>(testHarness);
    builder.Services.AddSingleton<WeaveFleet.Application.Harnesses.IHarnessRuntime>(sp =>
    {
        testHarnessRuntime.SetScopeFactory(sp.GetRequiredService<IServiceScopeFactory>());
        return testHarnessRuntime;
    });

    // Expose the harness instances for tests / drivers that want to push events directly.
    builder.Services.AddSingleton(testHarness);
    builder.Services.AddSingleton(testHarnessRuntime);
}
builder.AddFleetTelemetry();
builder.AddFleetDiagnosticLogging();
builder.Services.AddSingleton<WeaveFleet.Application.Services.ToolDetector>();
#pragma warning restore IL2026
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
            // Allow dev-domain frontend servers during split-mode development.
            // weave-fleet-dev.localhost avoids cookie conflicts with any
            // installed instance running on plain localhost.
            policy.WithOrigins(
                    "http://weave-fleet-dev.localhost:3001",
                    "https://weave-fleet-dev.localhost:3001",
                    "http://weave-fleet-dev.localhost:3002",
                    "https://weave-fleet-dev.localhost:3002")
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
            options.SaveTokens = false; // no bearer tokens stored in browser
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
    // Local mode: cookie authentication + local token service + passthrough IUserContext
    builder.Services.AddSingleton<LocalTokenAuthService>();
    builder.Services.AddSingleton<ILocalTokenAuthService>(sp =>
        sp.GetRequiredService<LocalTokenAuthService>());

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
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(fleetOptions.Auth.CookieExpirationMinutes);
            options.SlidingExpiration = true;
            options.LoginPath = "/login";

            options.Events.OnRedirectToLogin = async context =>
            {
                if (IsApiOrWebSocketRequest(context.Request.Path))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                // Auto-sign-in for localhost requests — no token challenge needed
                if (IsLocalhostRequest(context.HttpContext))
                {
                    var claims = new[]
                    {
                        new Claim(ClaimTypes.Name, "local"),
                        new Claim(ClaimTypes.NameIdentifier, "local"),
                        new Claim("sub", "local")
                    };

                    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                    var principal = new ClaimsPrincipal(identity);

                    await context.HttpContext.SignInAsync(
                        CookieAuthenticationDefaults.AuthenticationScheme, principal);

                    context.Response.Redirect(context.RedirectUri);
                    return;
                }

                context.Response.Redirect(context.RedirectUri);
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

            if (fleetOptions.Auth.TokenAuthEnabled)
            {
                options.ForwardDefaultSelector = context =>
                    HasBearerAuthorizationHeader(context.Request)
                        ? BearerTokenHandler.SchemeName
                        : null;
            }
        });

    if (fleetOptions.Auth.TokenAuthEnabled)
    {
        builder.Services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, BearerTokenHandler>(
                BearerTokenHandler.SchemeName,
                _ => { });
    }

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("FleetUser", policy =>
        {
            if (fleetOptions.Auth.TokenAuthEnabled)
                policy.AddAuthenticationSchemes(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    BearerTokenHandler.SchemeName);

            policy.RequireAuthenticatedUser();
        });
    });

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

// ── JSON Serialization ───────────────────────────────────────────────────────
// Register source-generated JsonSerializerContext so minimal API endpoints
// use compile-time serialization (required for trimming and AOT).
// DefaultJsonTypeInfoResolver is appended as a fallback so the runtime
// RequestDelegateFactory can resolve framework types (e.g. Task) for
// dynamically-registered plugin endpoints that bypass RDG interception.
JsonSerializationSetup.ConfigureHttpJson(builder.Services);

// Register ProblemDetails service so Results.Problem() serializes correctly
// with the source-generated context.
builder.Services.AddProblemDetails();

// Use Fleet.Host/Port as the listen URL (overrides the "Urls" config key)
builder.WebHost.UseUrls(fleetOptions.ListenUrl);

var app = builder.Build();

if (!fleetOptions.Auth.Enabled)
{
    var localTokenAuthService = app.Services.GetRequiredService<ILocalTokenAuthService>();
    Console.WriteLine();
    var displayHost = fleetOptions.Host is "0.0.0.0" or "::" ? Environment.MachineName : "localhost";
    Console.WriteLine($"  Access Weave Fleet at http://{displayHost}:{fleetOptions.Port}/login?token={localTokenAuthService.Token}");
    Console.WriteLine();
}

if (fleetOptions.Auth.Enabled)
{
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    });
}

// One-shot relocation of legacy data files (DB, analytics DB) from CWD into
// LocalAppData when the resolved paths are still defaults — no-op when overridden.
var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
LegacyDataMigrator.MigrateIfNeeded(fleetOptions, startupLogger);

// Back up the legacy Weave Agent Fleet DB before migrations can alter it.
LegacyDataMigrator.BackupLegacyAgentDb(fleetOptions.DatabasePath, startupLogger);

// Run database migrations at startup
var migrationRunner = app.Services.GetRequiredService<MigrationRunner>();
await migrationRunner.ApplyMigrationsAsync();

// Run analytics database migrations (only when analytics is enabled)
var analyticsMigrationRunner = app.Services.GetService<AnalyticsMigrationRunner>();
if (analyticsMigrationRunner is not null)
    await analyticsMigrationRunner.ApplyMigrationsAsync();

if (importLegacySessions)
    await RunLegacySessionImportAsync(app, legacyImportSourcePath, app.Lifetime.ApplicationStopping);

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
        await context.Response.WriteAsJsonAsync(
            new WeaveFleet.Api.ErrorResponse("Invalid or missing CSRF token."),
            WeaveFleet.Api.ApiJsonContext.Default.ErrorResponse);
    }
});

app.UseAuthentication();
app.UseAuthorization();

// WebSocket support (for /ws real-time events endpoint)
app.UseWebSockets();

// Health checks (registered before SPA fallback)
app.MapHealthChecks("/healthz");
app.MapHealthChecks("/readyz");
#pragma warning disable IL2026 // RDG intercepts this MapGet in a Web SDK project
app.MapGet("/version", () => Results.Ok(new VersionResponse(
    FleetInstrumentation.ServiceVersion,
    FleetInstrumentation.ServiceCommit)));
#pragma warning restore IL2026
app.MapAuthEndpoints(fleetOptions);

// API endpoints (registered before SPA fallback)
app.MapFleetEndpoints();

// Static file serving (SPA)
app.UseDefaultFiles(); // Serves index.html for "/"
app.UseStaticFiles(); // Serves files from wwwroot/

// SPA fallback — any unmatched route serves index.html for client-side routing
app.MapFallbackToFile("index.html")
    .AllowAnonymous();

await app.RunAsync();

static bool IsApiOrWebSocketRequest(PathString path)
    => path.StartsWithSegments("/api") || path.StartsWithSegments("/ws");

static bool IsLocalhostRequest(HttpContext context)
{
    var remoteIp = context.Connection.RemoteIpAddress;
    return remoteIp is not null && IPAddress.IsLoopback(remoteIp);
}

static bool HasBearerAuthorizationHeader(HttpRequest request)
{
    if (!request.Headers.TryGetValue(Microsoft.Net.Http.Headers.HeaderNames.Authorization, out var authorizationHeaderValues))
        return false;

    var authorizationHeader = authorizationHeaderValues.ToString();
    return authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
}

static async Task RunLegacySessionImportAsync(
    WebApplication app,
    string? sourcePathOverride,
    CancellationToken cancellationToken)
{
    var resolvedSourcePath = ResolveLegacyImportSourcePath(sourcePathOverride);

    try
    {
        using var scope = app.Services.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<ILegacySessionImporter>();
        var result = await importer.ImportAsync(resolvedSourcePath, cancellationToken);

        Console.WriteLine($"Legacy session import status: {result.Status}");
        Console.WriteLine($"Legacy session import count: {result.SessionCount}");
        Console.WriteLine($"Legacy session import source: {result.SourcePath}");

        Environment.Exit(string.Equals(result.Status, "not_found", StringComparison.Ordinal) ? 1 : 0);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Legacy session import failed: {ex.Message}");
        Console.Error.WriteLine("Legacy session import status: failed");
        Console.Error.WriteLine("Legacy session import count: 0");
        Console.Error.WriteLine($"Legacy session import source: {resolvedSourcePath}");

        Environment.Exit(1);
    }
}

static string ResolveLegacyImportSourcePath(string? sourcePathOverride)
{
    var sourcePath = string.IsNullOrWhiteSpace(sourcePathOverride)
        ? "~/.weave/fleet.db.legacy-backup"
        : sourcePathOverride;

    return ExpandUserHomePath(sourcePath);
}

static string ExpandUserHomePath(string path)
{
    if (!path.StartsWith("~/", StringComparison.Ordinal))
        return path;

    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    if (string.IsNullOrWhiteSpace(home))
        return path;

    return Path.Combine(home, path[2..].Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>Logger message definitions for startup diagnostics.</summary>
internal static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Recovery: marked {Instances} instance(s) and {Sessions} session(s) as stopped.")]
    public static partial void RecoveryComplete(ILogger logger, int instances, int sessions);

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
public partial class Program
{
}

/// <summary>
/// Configures HTTP JSON options with source-generated context and a reflection-based
/// fallback resolver. The fallback is required so the runtime <c>RequestDelegateFactory</c>
/// can resolve framework types (e.g. <see cref="Task"/>) for dynamically-registered
/// plugin endpoints that bypass RDG interception. The fallback is never used for actual
/// serialization — <see cref="ApiJsonContext"/> takes priority at position 0 in the chain.
/// </summary>
internal static class JsonSerializationSetup
{
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "DefaultJsonTypeInfoResolver is a fallback for framework types (e.g. Task) that are always preserved; it is never used for actual API payload serialization.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "DefaultJsonTypeInfoResolver is a fallback for framework types (e.g. Task) that are always preserved; it is never used for actual API payload serialization.")]
    public static void ConfigureHttpJson(IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
            options.SerializerOptions.TypeInfoResolverChain.Add(
                new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver());
        });
    }
}
