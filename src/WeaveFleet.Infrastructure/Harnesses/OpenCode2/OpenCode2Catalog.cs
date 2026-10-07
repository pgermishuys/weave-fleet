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
        await Task.WhenAll(agents, models, providers, defaultModel).ConfigureAwait(false);

        return ToCatalog(await agents, await providers, await models, await defaultModel);
    }

    public static async Task<IReadOnlyList<AgentInfo>> ReadAgentsAsync(OpenCode2Server server, string directory, CancellationToken ct)
    {
        await server.LoadLocationAsync(directory, ct).ConfigureAwait(false);
        var client = server.Client;
        return ToAgentInfos(await client.GetAgentsAsync(directory, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The model a session in <paramref name="directory"/> runs on when a prompt picks none: its agent's own, else the
    /// folder's default. V2 keeps the model on the session and never takes it from a primary agent (its TUI selects the
    /// agent's model itself), so Fleet selects it to give the model the pickers show as Default. <paramref name="agent"/>
    /// unset is V2's default agent.
    /// </summary>
    public static async Task<OpenCode2ModelRef?> ReadDefaultModelAsync(OpenCode2Server server, string directory, string? agent, CancellationToken ct)
    {
        await server.LoadLocationAsync(directory, ct).ConfigureAwait(false);
        var client = server.Client;
        if (AgentsOwnModel(await client.GetAgentsAsync(directory, ct).ConfigureAwait(false), agent) is { } own)
            return own;

        return await client.GetDefaultModelAsync(directory, ct).ConfigureAwait(false) is { Id: { Length: > 0 } id, ProviderId: { Length: > 0 } providerId }
            ? new OpenCode2ModelRef { Id = id, ProviderId = providerId }
            : null;
    }

    /// <summary>The model <paramref name="agent"/> (else V2's default agent) names for itself, if any.</summary>
    internal static OpenCode2ModelRef? AgentsOwnModel(IReadOnlyList<OpenCode2AgentInfo> agents, string? agent)
    {
        var info = string.IsNullOrWhiteSpace(agent) ? agents.FirstOrDefault(CanRunASession) : agents.FirstOrDefault(candidate => candidate.Id == agent);
        return info?.Model is { Id: { Length: > 0 }, ProviderId: { Length: > 0 } } model ? model : null;
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

    /// <summary>
    /// What the composer offers after <c>/</c>: V2's commands, then its skills. V2 lists them apart, and a skill runs
    /// through a prompt, not the command route (<see cref="FindSkillAsync"/>); a command of the same name wins.
    /// </summary>
    public static async Task<IReadOnlyList<CommandInfo>> ReadCommandsAsync(OpenCode2Server server, string directory, CancellationToken ct)
    {
        await server.LoadLocationAsync(directory, ct).ConfigureAwait(false);
        var commands = server.Client.GetCommandsAsync(directory, ct);
        var skills = SkillsOrNoneAsync(server, directory, ct);
        await Task.WhenAll(commands, skills).ConfigureAwait(false);
        return ToCommandInfos(await commands, await skills);
    }

    internal static IReadOnlyList<CommandInfo> ToCommandInfos(IReadOnlyList<OpenCode2CommandInfo> commands, IReadOnlyList<OpenCode2SkillInfo> skills)
    {
        var listed = commands
            .Where(command => !string.IsNullOrWhiteSpace(command.Name))
            .Select(command => new CommandInfo { Name = command.Name!, Description = command.Description })
            .ToList();
        var names = listed.Select(command => command.Name).ToHashSet(StringComparer.Ordinal);
        listed.AddRange(skills
            .Where(skill => !string.IsNullOrWhiteSpace(skill.Name) && names.Add(skill.Name!))
            .Select(skill => new CommandInfo { Name = skill.Name!, Description = skill.Description }));
        return listed;
    }

    /// <summary>
    /// The skill a <c>/name</c> runs: one named <paramref name="name"/> when no command is, else null, and the command
    /// route runs it. The session's folder is loaded already.
    /// </summary>
    public static async Task<OpenCode2PromptSkill?> FindSkillAsync(OpenCode2Server server, string directory, string name, CancellationToken ct)
    {
        if ((await SkillsOrNoneAsync(server, directory, ct).ConfigureAwait(false))
                .FirstOrDefault(skill => string.Equals(skill.Name, name, StringComparison.Ordinal)) is not { Id: { Length: > 0 } id })
        {
            return null;
        }

        var commands = await server.Client.GetCommandsAsync(directory, ct).ConfigureAwait(false);
        return commands.Any(command => string.Equals(command.Name, name, StringComparison.Ordinal))
            ? null
            : new OpenCode2PromptSkill { Id = id, Name = name };
    }

    /// <summary>V2's skills in <paramref name="directory"/>; none when it can't list them, so its commands still work.</summary>
    private static async Task<IReadOnlyList<OpenCode2SkillInfo>> SkillsOrNoneAsync(OpenCode2Server server, string directory, CancellationToken ct)
    {
        try
        {
            return await server.Client.GetSkillsAsync(directory, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    internal static HarnessCatalog ToCatalog(
        IReadOnlyList<OpenCode2AgentInfo> agents,
        IReadOnlyList<OpenCode2ProviderInfo> providers,
        IReadOnlyList<OpenCode2ModelInfo> models,
        OpenCode2ModelInfo? defaultModel)
        => new()
        {
            Agents = ToAgentInfos(agents),
            Providers = ToProviderInfos(providers, models),
            DefaultAgent = agents.FirstOrDefault(CanRunASession)?.Id,
            DefaultModelProviderId = defaultModel?.ProviderId,
            DefaultModelId = defaultModel?.Id,
        };

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

    /// <summary>
    /// V2 lists its default agent first: the config's <c>default_agent</c>, else one a plugin picked, else build, and
    /// always one that can run a session.
    /// </summary>
    private static bool CanRunASession(OpenCode2AgentInfo agent)
        => !string.IsNullOrWhiteSpace(agent.Id) && !agent.Hidden && agent.Mode != "subagent";
}
