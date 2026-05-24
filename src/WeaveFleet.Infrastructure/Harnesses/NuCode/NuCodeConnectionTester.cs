using Microsoft.Extensions.AI;
using NuCode.Providers;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Harnesses.NuCode;

/// <summary>
/// Tests NuCode connectivity by performing a minimal chat completion with the configured provider.
/// </summary>
internal sealed class NuCodeConnectionTester : INuCodeConnectionTester
{
    private readonly IUserPreferenceRepository _prefs;
    private readonly INuCodeCredentialStore _credentialStore;
    private readonly IProviderRegistry _registry;
    private readonly IChatClientFactory _chatClientFactory;

    public NuCodeConnectionTester(
        IUserPreferenceRepository prefs,
        INuCodeCredentialStore credentialStore,
        IProviderRegistry registry,
        IChatClientFactory chatClientFactory)
    {
        _prefs = prefs;
        _credentialStore = credentialStore;
        _registry = registry;
        _chatClientFactory = chatClientFactory;
    }

    public async Task<NuCodeConnectionTestResult> TestAsync(CancellationToken ct)
    {
        var providerId = await _prefs.GetAsync(NuCodePreferenceKeys.Provider).ConfigureAwait(false) ?? "copilot";
        return await TestCoreAsync(providerId, ct).ConfigureAwait(false);
    }

    public async Task<NuCodeConnectionTestResult> TestAsync(string providerId, CancellationToken ct)
    {
        return await TestCoreAsync(providerId, ct).ConfigureAwait(false);
    }

    private async Task<NuCodeConnectionTestResult> TestCoreAsync(string providerId, CancellationToken ct)
    {
        var modelId = await _prefs.GetAsync(NuCodePreferenceKeys.ModelId).ConfigureAwait(false) ?? "gpt-4o";
        var baseUrl = await _prefs.GetAsync(NuCodePreferenceKeys.BaseUrl).ConfigureAwait(false);

        var provider = _registry.GetById(providerId);
        if (provider is null)
        {
            return new NuCodeConnectionTestResult(
                Success: false,
                Error: $"Unknown provider '{providerId}'.",
                LatencyMs: 0);
        }

        // Load credentials from NuCode's own store
        var storedCreds = await _credentialStore.GetAllForProviderAsync(provider.Id, ct).ConfigureAwait(false);
        var credentials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cred in storedCreds)
        {
            credentials[cred.FieldKey] = cred.Value;
        }

        // Check required credentials
        foreach (var field in provider.CredentialFields)
        {
            if (field.Required && field.IsSecret && !provider.CredentialOptional
                && !credentials.ContainsKey(field.Key))
            {
                return new NuCodeConnectionTestResult(
                    Success: false,
                    Error: $"No credentials found for provider '{provider.DisplayName}'. Add them in Settings → Providers.",
                    LatencyMs: 0);
            }
        }

        // Build provider options
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(baseUrl))
            options["baseUrl"] = baseUrl;

        IChatClient? chatClient = null;
        try
        {
            chatClient = _chatClientFactory.Create(
                provider, modelId, credentials, options.Count > 0 ? options : null);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "ping")],
                cancellationToken: ct).ConfigureAwait(false);
            sw.Stop();

            return new NuCodeConnectionTestResult(Success: true, Error: null, LatencyMs: (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new NuCodeConnectionTestResult(Success: false, Error: ex.Message, LatencyMs: 0);
        }
        finally
        {
            chatClient?.Dispose();
        }
    }
}
