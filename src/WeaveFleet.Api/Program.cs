// Dev workflow:
//
// Mode A — Integrated (backend serves built SPA):
//   Terminal 1: cd client && npm run build
//   Terminal 2: dotnet run --project src/WeaveFleet.Api
//   → http://localhost:3000 (API + SPA)
//
// Mode B — Split (frontend dev server + backend API):
//   Terminal 1: dotnet run --project src/WeaveFleet.Api
//   Terminal 2: cd client && npm run dev:split
//   → Frontend: http://localhost:3001 (hot reload)
//   → Backend:  http://localhost:3000 (API only)

using WeaveFleet.Api.Endpoints;
using WeaveFleet.Api.Telemetry;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure;
using WeaveFleet.Infrastructure.Data;
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
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            // Allow frontend dev server at :3001 during split-mode development
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
        else
        {
            // Production: same-origin only (SPA is served from the same host)
            policy.WithOrigins($"http://{fleetOptions.Host}:{fleetOptions.Port}")
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
    });
});

var app = builder.Build();

// Run database migrations at startup
var migrationRunner = app.Services.GetRequiredService<MigrationRunner>();
await migrationRunner.ApplyMigrationsAsync();

// Run analytics database migrations (only when analytics is enabled)
var analyticsMigrationRunner = app.Services.GetService<AnalyticsMigrationRunner>();
if (analyticsMigrationRunner is not null)
    await analyticsMigrationRunner.ApplyMigrationsAsync();

// Ensure scratch project exists
using (var scope = app.Services.CreateScope())
{
    var projectService = scope.ServiceProvider.GetRequiredService<ProjectService>();
    await projectService.EnsureScratchProjectAsync();
}

// Recovery: mark all previously-running instances and non-terminal sessions as stopped
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var instanceService = scope.ServiceProvider.GetRequiredService<InstanceService>();
    var instanceCount = await instanceService.MarkAllStoppedAsync();
    var sessionCount = await instanceService.MarkAllNonTerminalSessionsStoppedAsync();
    if (instanceCount > 0 || sessionCount > 0)
        StartupLog.RecoveryComplete(logger, instanceCount, sessionCount);
}

// Middleware pipeline
app.UseCors();

// WebSocket support (for /ws real-time events endpoint)
app.UseWebSockets();

// Health checks (registered before SPA fallback)
app.MapHealthChecks("/healthz");
app.MapHealthChecks("/readyz");

// API endpoints (registered before SPA fallback)
app.MapFleetEndpoints();

// Static file serving (SPA)
app.UseDefaultFiles();   // Serves index.html for "/"
app.UseStaticFiles();    // Serves files from wwwroot/

// SPA fallback — any unmatched route serves index.html for client-side routing
app.MapFallbackToFile("index.html");

await app.RunAsync();

/// <summary>Logger message definitions for startup diagnostics.</summary>
internal static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Recovery: marked {Instances} instance(s) and {Sessions} session(s) as stopped.")]
    public static partial void RecoveryComplete(ILogger logger, int instances, int sessions);
}
