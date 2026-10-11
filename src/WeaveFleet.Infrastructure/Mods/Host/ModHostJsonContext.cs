using System.Text.Json.Serialization;
using WeaveFleet.Application.Mods.Host;

namespace WeaveFleet.Infrastructure.Mods.Host;

/// <summary>The payloads Fleet and the mod host exchange, camelCase on the wire. Source-generated for Native AOT.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(ModHostInitializeResult))]
[JsonSerializable(typeof(EmptyParams))]
internal sealed partial class ModHostJsonContext : JsonSerializerContext;

/// <summary><c>initialize</c>: the protocol Fleet speaks and its own version.</summary>
internal sealed record InitializeParams(int Protocol, string FleetVersion);

/// <summary><c>shutdown</c> takes no parameters: <c>{}</c>.</summary>
internal sealed record EmptyParams;
