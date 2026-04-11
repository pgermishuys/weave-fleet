using Duende.IdentityServer.Models;
using Duende.IdentityServer.Test;
using System.Security.Claims;

namespace WeaveFleet.IdP;

/// <summary>
/// In-memory configuration for the Duende IdentityServer test host.
/// Defines test clients, identity resources, API scopes, and test users.
/// </summary>
internal static class Config
{
    /// <summary>
    /// The client ID used by Fleet's OIDC middleware.
    /// Must match <c>Fleet:Auth:ClientId</c> in test factory configuration.
    /// </summary>
    public const string FleetClientId = "fleet-test-client";

    /// <summary>
    /// The client secret used by Fleet's OIDC middleware.
    /// Must match <c>Fleet:Auth:ClientSecret</c> in test factory configuration.
    /// </summary>
    public const string FleetClientSecret = "test-secret";

    public static IEnumerable<IdentityResource> IdentityResources =>
    [
        new IdentityResources.OpenId(),
        new IdentityResources.Profile(),
        new IdentityResources.Email(),
    ];

    public static IEnumerable<ApiScope> ApiScopes => [];

    /// <summary>
    /// Builds the Fleet test client with the given redirect URI base URLs.
    /// Redirect URIs accept any port on <c>app.dev.localhost</c> to support ephemeral port assignment.
    /// </summary>
    public static IEnumerable<Client> GetClients(string[] allowedRedirectUriPrefixes) =>
    [
        new Client
        {
            ClientId = FleetClientId,
            ClientName = "WeaveFleet Test Client",
            ClientSecrets = [new Secret(FleetClientSecret.Sha256())],

            AllowedGrantTypes = GrantTypes.Code,
            RequirePkce = true,
            RequireConsent = false,

            // Accept any redirect URI — this is a test-only IdP.
            // The fleet port is dynamic (ephemeral), so we cannot pin the exact URI at startup.
            RedirectUris = ["*"],
            PostLogoutRedirectUris = ["*"],
            FrontChannelLogoutUri = null,

            AllowedScopes = ["openid", "profile", "email"],
            AllowOfflineAccess = false,
            AlwaysIncludeUserClaimsInIdToken = true,
        }
    ];

    /// <summary>
    /// Test users available in the IdP.
    /// </summary>
    public static List<TestUser> TestUsers =>
    [
        new TestUser
        {
            SubjectId = "test-user-1",
            Username = "testuser",
            Password = "password",
            IsActive = true,
            Claims =
            [
                new Claim("email", "test@example.com"),
                new Claim("name", "Test User"),
                new Claim("given_name", "Test"),
                new Claim("family_name", "User"),
            ]
        },
        new TestUser
        {
            SubjectId = "new-user-1",
            Username = "newuser",
            Password = "password",
            IsActive = true,
            Claims =
            [
                new Claim("email", "new@example.com"),
                new Claim("name", "New User"),
                new Claim("given_name", "New"),
                new Claim("family_name", "User"),
            ]
        },
    ];
}
