namespace NuCode.Providers.Auth;

/// <summary>
/// Auth flow for providers that require no authentication (e.g. local Ollama, llama.cpp).
/// Always succeeds immediately.
/// </summary>
public sealed class NoAuthFlow : IAuthFlow
{
    /// <inheritdoc />
    public Task<AuthFlowResult> ExecuteAsync(
        ProviderDefinition provider,
        INuCodeCredentialStore store,
        CancellationToken ct = default) =>
        Task.FromResult<AuthFlowResult>(new AuthFlowResult.Success());
}
