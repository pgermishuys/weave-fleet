using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Shouldly;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// <c>GET /openapi/v1.json</c> builds a schema for every request body. A body with a default the serializer can't write
/// (<c>JsonElement x = default</c>) made the whole document answer 500 on the AOT build, where the agent skill sends
/// agents to look request bodies up.
/// </summary>
public sealed class OpenApiSchemaTests
{
    [Fact]
    public void Every_type_in_the_api_context_has_a_schema()
    {
        var failures = new List<string>();
        var options = ApiJsonContext.Default.Options;
        foreach (var type in typeof(ApiJsonContext).GetProperties().Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(JsonTypeInfo<>)).Select(p => p.PropertyType.GetGenericArguments()[0]))
        {
            try { options.GetJsonSchemaAsNode(type); }
            catch (Exception ex) { failures.Add($"{type}: {ex.GetType().Name} {ex.Message}"); }
        }
        failures.ShouldBeEmpty();
    }
}
