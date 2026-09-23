using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using WeaveFleet.Api.Tests.Infrastructure;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class WeaveConfigEndpointTests
{
    private static HttpClient CreateClient(ApiWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    [Fact]
    public async Task saving_with_an_unknown_source_is_refused()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = CreateClient(factory);

        var response = await client.PutAsJsonAsync("/api/weave", new JsonObject { ["source"] = "theirs", ["files"] = new JsonObject() });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task a_file_weave_doesnt_read_is_refused()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = CreateClient(factory);

        var response = await client.PutAsJsonAsync("/api/weave", new JsonObject
        {
            ["source"] = "fleet",
            ["files"] = new JsonObject { ["../opencode.json"] = "{}" },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?flavor=Weave")]
    [InlineData("?flavor=pi")]
    public async Task reading_the_users_own_files_needs_weave_or_legacy(string query)
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = CreateClient(factory);

        var response = await client.GetAsync($"/api/weave/own{query}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task the_users_own_files_are_never_read_with_sign_in_on()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true);
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/api/weave/own?flavor=weave");

        response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
    }
}
