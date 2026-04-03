// Dev workflow:
//
// Mode A — Integrated (backend serves built SPA):
//   Terminal 1: cd client && npm run build:spa
//   Terminal 2: dotnet run --project src/WeaveFleet.Api
//   → http://localhost:3000 (API + SPA)
//
// Mode B — Split (frontend dev server + backend API):
//   Terminal 1: dotnet run --project src/WeaveFleet.Api
//   Terminal 2: cd client && npm run dev:split
//   → Frontend: http://localhost:3001 (hot reload)
//   → Backend:  http://localhost:3000 (API only)

using WeaveFleet.Api.Endpoints;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Bind Fleet options
var fleetOptions = builder.Configuration
    .GetSection(FleetOptions.SectionName)
    .Get<FleetOptions>() ?? new FleetOptions();

// Configure services
builder.Services.Configure<FleetOptions>(
    builder.Configuration.GetSection(FleetOptions.SectionName));
builder.Services.AddFleetInfrastructure(fleetOptions);
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

// Middleware pipeline
app.UseCors();

// WebSocket support (for /ws stub endpoint)
app.UseWebSockets();

// Health checks (registered before SPA fallback)
app.MapHealthChecks("/healthz");
app.MapHealthChecks("/readyz");

// Stub API endpoints (registered before SPA fallback)
app.MapFleetEndpoints();

// Static file serving (SPA)
app.UseDefaultFiles();   // Serves index.html for "/"
app.UseStaticFiles();    // Serves files from wwwroot/

// SPA fallback — any unmatched route serves index.html for client-side routing
app.MapFallbackToFile("index.html");

await app.RunAsync();
