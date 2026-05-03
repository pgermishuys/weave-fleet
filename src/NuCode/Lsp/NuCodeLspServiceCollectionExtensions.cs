using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NuCode.Configuration;

namespace NuCode.Lsp;

/// <summary>
/// Extension methods for registering the built-in LSP server manager.
/// This is opt-in — consumers who don't call AddNuCodeLsp() can provide their own ILspService.
/// </summary>
public static class NuCodeLspServiceCollectionExtensions
{
    /// <summary>
    /// Adds the built-in LSP server manager that manages LSP server processes based on configuration.
    /// Routes requests by file extension to the appropriate LSP server.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddNuCodeLsp(this IServiceCollection services)
    {
        services.TryAddSingleton<ILspService>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<NuCodeOptions>>().Value;
            var configMonitor = sp.GetRequiredService<IOptionsMonitor<NuCodeConfig>>();
            var logger = sp.GetRequiredService<ILogger<LspServerManager>>();
            return new LspServerManager(options.WorkingDirectory, configMonitor, logger);
        });

        return services;
    }
}
