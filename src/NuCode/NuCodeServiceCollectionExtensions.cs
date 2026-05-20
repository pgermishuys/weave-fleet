using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NuCode.Agents;
using NuCode.Audit;
using NuCode.Configuration;
using NuCode.Events;
using NuCode.Lsp;
using NuCode.Mcp;
using NuCode.Plugins;
using NuCode.Sessions;
using NuCode.Tools;

namespace NuCode;

/// <summary>
/// Extension methods for registering NuCode services with dependency injection.
/// </summary>
public static class NuCodeServiceCollectionExtensions
{
    /// <summary>
    /// Adds NuCode services to the specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configure">An action to configure <see cref="NuCodeOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddNuCode(
        this IServiceCollection services,
        Action<NuCodeOptions> configure)
    {
        services.Configure(configure);
        return AddNuCodeCore(services);
    }

    /// <summary>
    /// Adds NuCode services to the specified <see cref="IServiceCollection"/> with default options.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddNuCode(this IServiceCollection services)
    {
        services.Configure<NuCodeOptions>(_ => { });
        return AddNuCodeCore(services);
    }

    private static IServiceCollection AddNuCodeCore(IServiceCollection services)
    {
        // Configuration: multi-layer loader, options factory, file change watching
        services.TryAddSingleton<IConfigLoader>(sp =>
        {
            var nuCodeOptions = sp.GetRequiredService<IOptions<NuCodeOptions>>().Value;
            var loggerFactory = sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>();
            return new ConfigLoader(nuCodeOptions.WorkingDirectory, nuCodeOptions.Config, loggerFactory);
        });

        services.TryAddSingleton<IOptionsFactory<NuCodeConfig>>(sp =>
            new NuCodeConfigFactory(sp.GetRequiredService<IConfigLoader>()));

        services.TryAddSingleton<IOptionsChangeTokenSource<NuCodeConfig>>(sp =>
        {
            var nuCodeOptions = sp.GetRequiredService<IOptions<NuCodeOptions>>().Value;
            return new ConfigFileChangeTokenSource(nuCodeOptions.WorkingDirectory);
        });

        // Agent profiles
        services.TryAddSingleton<IAgentProfileRegistry, AgentProfileRegistry>();

        // Agent factory
        services.TryAddSingleton<INuCodeAgentFactory>(sp =>
            new NuCodeAgentFactory(
                sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>(),
                sp,
                sp.GetService<IOptionsMonitor<NuCodeConfig>>()));

        // Tool registry (auto-populated with built-in tools on creation)
        services.TryAddSingleton<IToolRegistry>(sp =>
        {
            var registry = new ToolRegistry();
            RegisterBuiltInTools(registry, sp);
            return registry;
        });

        // Event bus: scoped per session, global singleton for cross-session events
        services.TryAddScoped<INuCodeEventBus, NuCodeEventBus>();
        services.TryAddSingleton<GlobalEventBus>();

        // Session store (default: in-memory SQLite; consumers can override)
        services.TryAddSingleton<ISessionStore>(_ => new SqliteSessionStore("Data Source=:memory:"));

        // Session service (orchestrates lifecycle, publishes events)
        services.TryAddScoped<ISessionService, SessionService>();

        // Session processor (streaming response → message parts)
        services.TryAddScoped<ISessionProcessor, SessionProcessor>();

        // Compaction service (conversation context compaction)
        services.TryAddScoped<ICompactionService>(sp =>
        {
            var configMonitor = sp.GetRequiredService<IOptionsMonitor<NuCodeConfig>>();
            return new CompactionService(
                sp.GetRequiredService<ISessionService>(),
                sp.GetRequiredService<IPluginRegistry>(),
                sp.GetRequiredService<IChatClient>(),
                configMonitor.CurrentValue.Compaction,
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                sp.GetRequiredService<ILoggerFactory>()
                    .CreateLogger<CompactionService>());
        });

        // Plugin registry (manages plugins and hook triggering)
        services.TryAddSingleton<IPluginRegistry>(sp =>
            new PluginRegistry(
                sp,
                sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()));

        // MCP manager (manages MCP server connections and tools)
        services.TryAddSingleton<IMcpManager>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<NuCodeOptions>>().Value;
            var loggerFactory = sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>();
            return new McpManager(options.McpServers, new McpClientFactory(), loggerFactory);
        });

        // MCP tool registration (bridges MCP tools into the tool registry)
        services.TryAddSingleton(sp => new McpToolRegistration(
            sp.GetRequiredService<IMcpManager>(),
            sp.GetRequiredService<IToolRegistry>(),
            sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()));

        // Skill provider (file-based discovery from config + .nucode/skills/)
        services.TryAddSingleton<ISkillProvider>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<NuCodeOptions>>().Value;
            var configMonitor = sp.GetRequiredService<IOptionsMonitor<NuCodeConfig>>();
            return new FileSkillProvider(options.WorkingDirectory, configMonitor);
        });

        // Question service (deferred ask/reply for LLM → user questions)
        services.TryAddSingleton<IQuestionService, QuestionService>();

        // Audit service (singleton: manages per-session JSONL channels)
        services.TryAddSingleton<IAuditService>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<NuCodeOptions>>().Value;
            return new JsonlAuditService(options.WorkingDirectory);
        });

        // Audit event subscriber (scoped: wires to per-session event bus)
        services.TryAddScoped<AuditEventSubscriber>();

        return services;
    }

    private static void RegisterBuiltInTools(IToolRegistry registry, IServiceProvider sp)
    {
        // Core tools (no dependencies)
        TryRegister(registry, new ReadTool());
        TryRegister(registry, new WriteTool());
        TryRegister(registry, new EditTool());
        TryRegister(registry, new MultiEditTool());
        TryRegister(registry, new GlobTool());
        TryRegister(registry, new GrepTool());
        TryRegister(registry, new BashTool());
        TryRegister(registry, new TodoReadTool());
        TryRegister(registry, new TodoWriteTool());
        TryRegister(registry, new ApplyPatchTool());

        // Tools with dependencies
        TryRegister(registry, new WebFetchTool(new HttpClient()));
        TryRegister(registry, new SkillTool(sp.GetRequiredService<ISkillProvider>()));
        TryRegister(registry, new QuestionTool(sp.GetRequiredService<IQuestionService>()));

        // LSP tool — always register, degrades gracefully if no ILspService
        var lspService = sp.GetService<ILspService>();
        TryRegister(registry, new LspTool(lspService));

        // WebSearch — only register if a provider is available
        var searchProvider = sp.GetService<IWebSearchProvider>();
        if (searchProvider is not null)
        {
            TryRegister(registry, new WebSearchTool(searchProvider));
        }
    }

    private static void TryRegister(IToolRegistry registry, INuCodeTool tool)
    {
        try { registry.Register(tool); }
        catch (InvalidOperationException) { /* already registered by consumer */ }
    }
}
