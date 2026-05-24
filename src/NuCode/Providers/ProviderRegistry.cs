namespace NuCode.Providers;

/// <summary>
/// Default implementation of <see cref="IProviderRegistry"/>.
/// Seeded with built-in providers; supports runtime registration of custom providers.
/// </summary>
public sealed class ProviderRegistry : IProviderRegistry
{
    private const string DefaultProviderId = "copilot";

    private readonly Dictionary<string, ProviderDefinition> _providers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new registry pre-populated with the given provider definitions.
    /// </summary>
    public ProviderRegistry(IEnumerable<ProviderDefinition> providers)
    {
        foreach (var provider in providers)
        {
            _providers[provider.Id] = provider;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ProviderDefinition> GetAll() =>
        [.. _providers.Values];

    /// <inheritdoc />
    public ProviderDefinition? GetById(string providerId) =>
        _providers.TryGetValue(providerId, out var def) ? def : null;

    /// <inheritdoc />
    public string InferFromModelId(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return DefaultProviderId;

        // Explicit prefix notation: "copilot/claude-sonnet-4-20250514"
        var slashIndex = modelId.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex > 0)
        {
            var prefix = modelId[..slashIndex];
            if (_providers.ContainsKey(prefix))
                return prefix;
        }

        // Match against provider model prefixes (longest match wins)
        string? bestMatch = null;
        var bestLength = 0;

        foreach (var provider in _providers.Values)
        {
            foreach (var modelPrefix in provider.ModelPrefixes)
            {
                if (modelId.StartsWith(modelPrefix, StringComparison.OrdinalIgnoreCase)
                    && modelPrefix.Length > bestLength)
                {
                    bestMatch = provider.Id;
                    bestLength = modelPrefix.Length;
                }
            }
        }

        return bestMatch ?? DefaultProviderId;
    }

    /// <inheritdoc />
    public void Register(ProviderDefinition definition) =>
        _providers[definition.Id] = definition;
}
