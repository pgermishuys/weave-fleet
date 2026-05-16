using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class UpdateEndpointTests
{
    [Fact]
    public async Task get_update_status_returns_ok_with_current_version()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/update/status");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<UpdateStatusDto>(JsonSerializerOptions.Web);
        body.ShouldNotBeNull();
        body.CurrentVersion.ShouldNotBeNullOrEmpty();
        body.Status.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task get_update_status_reflects_seeded_state()
    {
        await using var factory = new ApiWebApplicationFactory(
            authEnabled: false,
            configureTestServices: services =>
            {
                // Pre-seed a known state so we can assert what the endpoint returns.
                var holder = new UpdateStateHolder();
                holder.SetState(new UpdateState(
                    UpdateStatus.Available,
                    LatestVersion: "99.0.0",
                    DownloadUrl: "https://example.com/fleet.zip",
                    AssetName: "fleet-v99.0.0-linux-x64.tar.gz",
                    CheckedAt: DateTimeOffset.UtcNow,
                    Error: null));

                services.AddSingleton(holder);
            });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/update/status");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<UpdateStatusDto>(JsonSerializerOptions.Web);
        body.ShouldNotBeNull();
        body.Status.ShouldBe("available");
        body.LatestVersion.ShouldBe("99.0.0");
    }

    [Fact]
    public async Task post_update_check_returns_accepted()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/update/check", content: null);

        // The endpoint triggers an async check; it returns Accepted even if the check
        // finds no update (e.g. because we're in a dev layout during tests).
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task post_update_download_returns_bad_request_when_no_update_available()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        // Default state is Unknown — not Available — so download should be rejected.
        var response = await client.PostAsync("/api/update/download", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private sealed record UpdateStatusDto(
        string CurrentVersion,
        string Status,
        string? LatestVersion,
        string? CheckedAt,
        string? Error);
}
