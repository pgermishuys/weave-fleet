extern alias IdpHost;

using global::Duende.IdentityServer.Models;
using IdpHost::WeaveFleet.IdP;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.E2E.Infrastructure;

/// <summary>
/// Starts the Duende IdentityServer test IdP as an in-process <see cref="WebApplication"/>
/// on <c>https://127.0.0.1:{dynamic-port}</c> using the .NET dev certificate for TLS.
///
/// Lifecycle:
/// <list type="number">
///   <item>Call <see cref="StartAsync"/> — binds to an ephemeral HTTPS port and starts Kestrel.</item>
///   <item>Read <see cref="Authority"/> — the OIDC authority URL for Fleet configuration.</item>
///   <item>Call <see cref="DisposeAsync"/> — stops Kestrel and releases all resources.</item>
/// </list>
/// </summary>
internal sealed class IdpProcessHost : IAsyncDisposable
{
    private WebApplication? _app;
    private string? _authority;

    /// <summary>
    /// The OIDC authority URL (e.g. <c>https://127.0.0.1:7401</c>).
    /// Only available after <see cref="StartAsync"/> completes.
    /// </summary>
    public string Authority => _authority
        ?? throw new InvalidOperationException(
            "IdpProcessHost has not been started. Call StartAsync() first.");

    /// <summary>
    /// Starts the IdP and returns once Kestrel is listening.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder();

        // Suppress IdP console logging during tests — only log warnings and above
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddRazorPages()
            .AddApplicationPart(typeof(IdpHost::WeaveFleet.IdP.Config).Assembly);

        builder.Services
            .AddIdentityServer(options =>
            {
                options.Events.RaiseErrorEvents = true;
                options.Events.RaiseFailureEvents = true;
                options.Events.RaiseSuccessEvents = false;
                options.Events.RaiseInformationEvents = false;
                options.EmitStaticAudienceClaim = true;
                // Disable discovery cache so we always reflect the actual runtime port
                options.Discovery.ResponseCacheInterval = 0;
                // Match the Razor Pages route for login/logout (Pages/Login/Index.cshtml → /Login)
                options.UserInteraction.LoginUrl = "/Login";
                options.UserInteraction.LogoutUrl = "/Logout";
            })
            .AddInMemoryIdentityResources(Config.IdentityResources)
            .AddInMemoryApiScopes(Config.ApiScopes)
            .AddInMemoryClients(BuildClients())
            .AddTestUsers(Config.TestUsers)
            .AddRedirectUriValidator<IdpHost::WeaveFleet.IdP.PermissiveRedirectUriValidator>();

        // Bind to HTTPS on 127.0.0.1 with an ephemeral port using the .NET dev certificate
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(
                System.Net.IPAddress.Loopback,
                port: 0,
                opts => opts.UseHttps());
        });

        // Override the host URL so Kestrel doesn't try to bind default 5000/5001
        builder.WebHost.UseUrls();

        _app = builder.Build();

        _app.UseStaticFiles();
        _app.UseRouting();
        _app.UseIdentityServer();
        _app.UseAuthorization();
        _app.MapRazorPages();

        await _app.StartAsync(cancellationToken);

        // Extract the bound port and build the authority URL
        var server = _app.Services.GetRequiredService<IServer>();
        var addressFeature = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException(
                "IServerAddressesFeature not available after IdP start.");

        var address = addressFeature.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "IdP Kestrel did not report any listening addresses.");

        // Kestrel reports the address as https://0.0.0.0:{port} or https://127.0.0.1:{port}
        // Use the raw IP that Kestrel reports — *.dev.localhost and localhost may not
        // resolve (or may resolve to ::1) on some platforms. 127.0.0.1 is guaranteed.
        var uri = new Uri(address);
        _authority = $"https://127.0.0.1:{uri.Port}";
    }

    /// <summary>
    /// Builds the test client list. Uses a wildcard redirect URI because Fleet's port
    /// is dynamic (ephemeral) and is not known when the IdP starts.
    /// </summary>
    private static IEnumerable<Client> BuildClients()
    {
        // Return a copy of the default clients — IdP accepts any redirect URI for test simplicity
        foreach (var client in Config.GetClients([]))
            yield return client;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }
}
