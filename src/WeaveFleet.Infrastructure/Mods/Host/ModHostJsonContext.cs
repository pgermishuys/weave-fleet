using System.Text.Json.Serialization;
using WeaveFleet.Application.Mods.Host;

namespace WeaveFleet.Infrastructure.Mods.Host;

/// <summary>
/// Every payload Fleet and the mod host exchange (the <c>fleet-mods/protocol</c> types), camelCase on the wire, with
/// null fields left out. Source-generated so it works under Native AOT. The JSON-RPC envelopes themselves are written
/// and read by <see cref="ModHostRpc"/> directly.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(ModHostInitializeResult))]
[JsonSerializable(typeof(CheckParams))]
[JsonSerializable(typeof(ModLoadParams))]
[JsonSerializable(typeof(ModLoadResult))]
[JsonSerializable(typeof(ModHookSpec))]
[JsonSerializable(typeof(UnloadParams))]
[JsonSerializable(typeof(ForgetParams))]
[JsonSerializable(typeof(EmptyParams))]
[JsonSerializable(typeof(ModWireDispatch))]
[JsonSerializable(typeof(ModWireDispatchResult))]
[JsonSerializable(typeof(ModHookFailure))]
internal sealed partial class ModHostJsonContext : JsonSerializerContext;

/// <summary><c>initialize</c>: the protocol Fleet speaks and its own version.</summary>
internal sealed record InitializeParams(int Protocol, string FleetVersion);

/// <summary><c>check</c>: the folder and its <c>mod.json</c>.</summary>
internal sealed record CheckParams(string Root, string Manifest);

/// <summary><c>unload</c>.</summary>
internal sealed record UnloadParams(string Id);

/// <summary><c>forget</c>: a session ended.</summary>
internal sealed record ForgetParams(string SessionId);

/// <summary><c>shutdown</c> takes no parameters: <c>{}</c>.</summary>
internal sealed record EmptyParams;
