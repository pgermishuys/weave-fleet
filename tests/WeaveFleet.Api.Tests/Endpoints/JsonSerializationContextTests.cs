using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class JsonSerializationContextTests
{
    [Fact]
    public async Task ApiJsonContext_resolves_all_endpoint_request_body_types()
    {
        // This test validates that every type used as a JSON request body parameter
        // in minimal API endpoints is registered in ApiJsonContext. Without this, the app
        // will crash at startup under NativeAOT/trimming where reflection-based serialization
        // is unavailable. The crash occurs during endpoint pipeline construction when the
        // framework tries to resolve JsonTypeInfo for the request body type.
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        var resolver = ApiJsonContext.Default.Options.TypeInfoResolver!;
        var options = ApiJsonContext.Default.Options;

        var endpointSources = factory.Services
            .GetRequiredService<IEnumerable<Microsoft.AspNetCore.Routing.EndpointDataSource>>();

        var missingTypes = new List<string>();

        foreach (var source in endpointSources)
        {
            foreach (var endpoint in source.Endpoints)
            {
                foreach (var accepts in endpoint.Metadata.GetOrderedMetadata<IAcceptsMetadata>())
                {
                    if (accepts.RequestType is { } requestType
                        && requestType != typeof(string)
                        && !requestType.IsPrimitive)
                    {
                        if (resolver.GetTypeInfo(requestType, options) is null)
                        {
                            missingTypes.Add(
                                $"Request body type '{requestType.FullName}' is missing from ApiJsonContext (endpoint: {endpoint.DisplayName})");
                        }
                    }
                }
            }
        }

        Assert.True(missingTypes.Count == 0,
            $"The following types are missing from ApiJsonContext:\n{string.Join("\n", missingTypes)}");
    }
}
