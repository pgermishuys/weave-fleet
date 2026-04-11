using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using WeaveFleet.Api.Tests.Infrastructure;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class CredentialEndpointTests
{
    [Fact]
    public async Task PutAndGetCredentials_WithCsrfToken_StoresAndReturnsMetadataOnly()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var csrfToken = await GetCsrfTokenAsync(client);
        var secretValue = "sk-anthropic-secret-1234";

        using var storeRequest = CreateJsonRequest(
            HttpMethod.Put,
            "/api/credentials",
            new StoreCredentialPayload("Work Key", "anthropic", "api-key", secretValue, "{\"scope\":\"work\"}"),
            csrfToken);

        var storeResponse = await client.SendAsync(storeRequest);

        storeResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var listResponse = await client.GetAsync("/api/credentials");
        var listBody = await listResponse.Content.ReadAsStringAsync();

        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        listBody.ShouldNotContain(secretValue);

        var credentials = JsonSerializer.Deserialize<List<CredentialResponsePayload>>(listBody, JsonSerializerOptions.Web);
        credentials.ShouldNotBeNull();
        credentials.Count.ShouldBe(1);
        credentials[0].Label.ShouldBe("Work Key");
        credentials[0].Namespace.ShouldBe("anthropic");
        credentials[0].Kind.ShouldBe("api-key");
        credentials[0].DisplayHint.ShouldBe("...1234");
        credentials[0].Metadata.ShouldBe("{\"scope\":\"work\"}");

        using var json = JsonDocument.Parse(listBody);
        var credential = json.RootElement[0];
        credential.TryGetProperty("value", out _).ShouldBeFalse();
        credential.TryGetProperty("encryptedValue", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task PutById_WithCsrfToken_UpdatesCredentialValueAndMetadata()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var csrfToken = await GetCsrfTokenAsync(client);

        using var createRequest = CreateJsonRequest(
            HttpMethod.Put,
            "/api/credentials",
            new StoreCredentialPayload("Shared Key", "openai", "api-key", "sk-openai-old-1111", "{\"scope\":\"old\"}"),
            csrfToken);
        var createResponse = await client.SendAsync(createRequest);
        createResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var initialCredentials = await client.GetFromJsonAsync<List<CredentialResponsePayload>>("/api/credentials");
        initialCredentials.ShouldNotBeNull();
        initialCredentials.Count.ShouldBe(1);

        var credentialId = initialCredentials[0].Id;

        using var updateRequest = CreateJsonRequest(
            HttpMethod.Put,
            $"/api/credentials/{credentialId}",
            new UpdateCredentialPayload("sk-openai-new-5678", "{\"scope\":\"new\"}"),
            csrfToken);

        var updateResponse = await client.SendAsync(updateRequest);

        updateResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var listResponse = await client.GetAsync("/api/credentials");
        var listBody = await listResponse.Content.ReadAsStringAsync();
        listBody.ShouldNotContain("sk-openai-old-1111");
        listBody.ShouldNotContain("sk-openai-new-5678");

        var updatedCredentials = JsonSerializer.Deserialize<List<CredentialResponsePayload>>(listBody, JsonSerializerOptions.Web);
        updatedCredentials.ShouldNotBeNull();
        updatedCredentials.Count.ShouldBe(1);
        updatedCredentials[0].Id.ShouldBe(credentialId);
        updatedCredentials[0].Label.ShouldBe("Shared Key");
        updatedCredentials[0].Namespace.ShouldBe("openai");
        updatedCredentials[0].Kind.ShouldBe("api-key");
        updatedCredentials[0].DisplayHint.ShouldBe("...5678");
        updatedCredentials[0].Metadata.ShouldBe("{\"scope\":\"new\"}");
    }

    [Fact]
    public async Task Delete_WithCsrfToken_RemovesCredential()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var csrfToken = await GetCsrfTokenAsync(client);

        using var createRequest = CreateJsonRequest(
            HttpMethod.Put,
            "/api/credentials",
            new StoreCredentialPayload("Delete Me", "custom", "api-key", "delete-secret-9999", null),
            csrfToken);
        var createResponse = await client.SendAsync(createRequest);
        createResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var credentials = await client.GetFromJsonAsync<List<CredentialResponsePayload>>("/api/credentials");
        credentials.ShouldNotBeNull();
        credentials.Count.ShouldBe(1);

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/credentials/{credentials[0].Id}");
        deleteRequest.Headers.Add("X-CSRF-Token", csrfToken);

        var deleteResponse = await client.SendAsync(deleteRequest);

        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var afterDelete = await client.GetFromJsonAsync<List<CredentialResponsePayload>>("/api/credentials");
        afterDelete.ShouldNotBeNull();
        afterDelete.ShouldBeEmpty();
    }

    private static HttpRequestMessage CreateJsonRequest<T>(HttpMethod method, string uri, T payload, string csrfToken)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("X-CSRF-Token", csrfToken);
        return request;
    }

    private static async Task<string> GetCsrfTokenAsync(HttpClient client)
    {
        var meResponse = await client.GetAsync("/api/user/me");
        meResponse.EnsureSuccessStatusCode();

        var csrfToken = meResponse.Headers.TryGetValues("Set-Cookie", out var setCookies)
            ? ExtractCookieValue(setCookies, ".WeaveFleet.CSRF")
            : null;

        csrfToken.ShouldNotBeNull();
        return csrfToken;
    }

    private static string? ExtractCookieValue(IEnumerable<string> setCookies, string cookieName)
    {
        foreach (var header in setCookies)
        {
            if (!header.StartsWith(cookieName + "=", StringComparison.Ordinal))
                continue;

            var endIndex = header.IndexOf(';');
            return endIndex >= 0
                ? header.Substring(cookieName.Length + 1, endIndex - cookieName.Length - 1)
                : header.Substring(cookieName.Length + 1);
        }

        return null;
    }

    private sealed record StoreCredentialPayload(string Label, string Namespace, string Kind, string Value, string? Metadata);
    private sealed record UpdateCredentialPayload(string Value, string? Metadata);
    private sealed record CredentialResponsePayload(
        string Id,
        string Label,
        string Namespace,
        string Kind,
        string DisplayHint,
        string? Metadata,
        string CreatedAt,
        string UpdatedAt);
}
