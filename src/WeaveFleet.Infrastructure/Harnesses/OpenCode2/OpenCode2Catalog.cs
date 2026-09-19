using System.Text.Json;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// What a V2 server offers in a folder (agents, models, commands) as Fleet's catalog. V2 serves every folder from one
/// server and reads a folder's config only once it's loaded, so every read waits for the folder to be loaded first.
/// </summary>
internal static class OpenCode2Catalog
{
    /// <summary>The agents and models for a new session in <paramref name="directory"/>, and what it gets by default.</summary>
    public static async Task<HarnessCatalog> ReadAsync(OpenCode2Server server, string directory, CancellationToken ct)
    {
        await server.LoadLocationAsync(directory, ct).ConfigureAwait(false);
        var client = server.Client;
        var agents = client.GetAgentsAsync(directory, ct);
        var models = client.GetModelsAsync(directory, ct);
        var providers = client.GetProvidersAsync(directory, ct);
        var defaultModel = client.GetDefaultModelAsync(directory, ct);
        var config = client.GetConfigAsync(directory, ct);
        await Task.WhenAll(agents, models, providers, defaultModel, config).ConfigureAwait(false);

        return ToCatalog(await agents, await providers, await models, await defaultModel, DefaultAgent(await config));
    }

    public static async Task<IReadOnlyList<AgentInfo>> ReadAgentsAsync(OpenCode2Server server, string directory, CancellationToken ct)
    {
        await server.LoadLocationAsync(directory, ct).ConfigureAwait(false);
        var client = server.Client;
        return ToAgentInfos(await client.GetAgentsAsync(directory, ct).ConfigureAwait(false));
    }

    public static async Task<IReadOnlyList<ProviderInfo>> ReadProvidersAsync(OpenCode2Server server, string directory, CancellationToken ct)
    {
        await server.LoadLocationAsync(directory, ct).ConfigureAwait(false);
        var client = server.Client;
        var models = client.GetModelsAsync(directory, ct);
        var providers = client.GetProvidersAsync(directory, ct);
        await Task.WhenAll(models, providers).ConfigureAwait(false);
        return ToProviderInfos(await providers, await models);
    }

    public static async Task<IReadOnlyList<CommandInfo>> ReadCommandsAsync(OpenCode2Server server, string directory, CancellationToken ct)
    {
        await server.LoadLocationAsync(directory, ct).ConfigureAwait(false);
        var client = server.Client;
        return (await client.GetCommandsAsync(directory, ct).ConfigureAwait(false))
            .Where(command => !string.IsNullOrWhiteSpace(command.Name))
            .Select(command => new CommandInfo { Name = command.Name!, Description = command.Description })
            .ToList();
    }

    internal static HarnessCatalog ToCatalog(
        IReadOnlyList<OpenCode2AgentInfo> agents,
        IReadOnlyList<OpenCode2ProviderInfo> providers,
        IReadOnlyList<OpenCode2ModelInfo> models,
        OpenCode2ModelInfo? defaultModel,
        string? configuredDefaultAgent)
    {
        var agentInfos = ToAgentInfos(agents);

        // V2's own rule: the configured default when it's a visible agent that can run a session, else build, else
        // the first such agent.
        var selectable = agentInfos.Where(agent => !agent.Hidden && agent.Mode != "subagent").ToList();
        var defaultAgent = selectable.FirstOrDefault(agent => agent.Name == configuredDefaultAgent)
            ?? selectable.FirstOrDefault(agent => agent.Name == "build")
            ?? selectable.FirstOrDefault();

        return new HarnessCatalog
        {
            Agents = agentInfos,
            Providers = ToProviderInfos(providers, models),
            DefaultAgent = defaultAgent?.Name,
            DefaultModelProviderId = defaultModel?.ProviderId,
            DefaultModelId = defaultModel?.Id,
        };
    }

    /// <summary>A session is switched to an agent by its id, so that's the name Fleet shows and sends.</summary>
    internal static IReadOnlyList<AgentInfo> ToAgentInfos(IEnumerable<OpenCode2AgentInfo> agents)
        => agents
            .Where(agent => !string.IsNullOrWhiteSpace(agent.Id))
            .Select(agent => new AgentInfo
            {
                Name = agent.Id!,
                Mode = agent.Mode,
                Hidden = agent.Hidden,
                ModelProviderId = agent.Model?.ProviderId,
                ModelId = agent.Model?.Id,
            })
            .ToList();

    /// <summary>
    /// The models a session can use, by provider: V2 lists the models of the providers it can reach, and a provider
    /// the user turned off (or a model) isn't offered. Variants are what Fleet's effort picks from.
    /// </summary>
    internal static IReadOnlyList<ProviderInfo> ToProviderInfos(
        IReadOnlyList<OpenCode2ProviderInfo> providers,
        IReadOnlyList<OpenCode2ModelInfo> models)
        => providers
            .Where(provider => !string.IsNullOrWhiteSpace(provider.Id) && provider.Activation != "disabled")
            .Select(provider => new ProviderInfo
            {
                Id = provider.Id!,
                Name = provider.Name,
                Models = models
                    .Where(model => model.ProviderId == provider.Id && !string.IsNullOrWhiteSpace(model.Id) && model.Enabled != false)
                    .Select(model => new ModelInfo
                    {
                        Id = model.Id!,
                        Name = model.Name,
                        Variants = model.Variants?.Select(v => v.Id).OfType<string>().ToList() is { Count: > 0 } variants ? variants : null,
                    })
                    .ToList(),
            })
            .Where(provider => provider.Models.Count > 0)
            .ToList();

    /// <summary>The <c>default_agent</c> of the last config document that sets one (a folder's own config wins).</summary>
    internal static string? DefaultAgent(IEnumerable<OpenCode2ConfigSource> config)
        => config
            .Where(source => source.Info.ValueKind == JsonValueKind.Object)
            .Select(source => source.Info.TryGetProperty("default_agent", out var agent) && agent.ValueKind == JsonValueKind.String
                ? agent.GetString()
                : null)
            .LastOrDefault(agent => !string.IsNullOrWhiteSpace(agent));
}
