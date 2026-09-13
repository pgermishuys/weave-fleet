using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Tools;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Skills;
using WeaveFleet.Domain.Tools;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Tools;

/// <summary>
/// Installs tools where OpenCode loads them, globally or into a repository (see <see cref="HarnessInstallPaths"/>):
/// native tools as <c>tools/&lt;name&gt;.ts</c>, MCP servers under <c>mcp</c> in <c>opencode.json</c>.
/// Never replaces a file or server entry Fleet didn't write, unless it's identical.
/// </summary>
public sealed partial class ToolInstaller : IToolInstaller
{
    /// <summary>Where older Fleet versions registered MCP servers. OpenCode never read it.</summary>
    internal const string LegacyMcpKey = "mcpServers";

    private const string NewConfig = "{\n  \"$schema\": \"https://opencode.ai/config.json\"\n}\n";

    private static readonly string[] ToolExtensions = [".ts", ".js"];

    // OpenCode reads opencode.json and opencode.jsonc from the same folder and merges them.
    private static readonly string[] ConfigFileNames = ["opencode.json", "opencode.jsonc"];

    private static readonly JsonDocumentOptions ConfigReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly HarnessInstallPaths _paths;
    private readonly ILogger<ToolInstaller> _logger;
    private readonly IHarnessPoolRecycler? _poolRecycler;

    public ToolInstaller(HarnessInstallPaths paths, ILogger<ToolInstaller> logger, IHarnessPoolRecycler? poolRecycler = null)
    {
        _paths = paths;
        _logger = logger;
        _poolRecycler = poolRecycler;
    }

    public async Task<Result<string>> InstallNativeAsync(
        string name,
        string sourcePath,
        InstallTarget target,
        CancellationToken cancellationToken = default)
    {
        if (ValidateName(name) is { } invalid)
            return invalid;

        var sourceFile = ResolveToolFile(name, sourcePath);
        if (sourceFile.IsFailure)
            return sourceFile.Error;

        var toolsDir = _paths.OpenCodeToolsDirectory(target);
        var targetPath = Path.Combine(toolsDir, name + Path.GetExtension(sourceFile.Value).ToLowerInvariant());

        if (InstalledFiles.SamePath(sourceFile.Value, targetPath))
            return FleetError.ValidationError("ToolSource", $"{targetPath} is already in OpenCode's tools folder.");

        // OpenCode loads <name>.ts and <name>.js alike, so either one already there clashes.
        foreach (var existing in ToolExtensions.Select(ext => Path.Combine(toolsDir, name + ext)).Where(InstalledFiles.Exists))
        {
            if (!InstalledFiles.SamePath(existing, targetPath) || !InstalledFiles.FilesMatch(sourceFile.Value, existing))
            {
                return Conflict($"{existing} already exists and wasn't installed by Fleet. Remove or rename it, then install again.");
            }
        }

        try
        {
            Directory.CreateDirectory(toolsDir);

            // Copy beside the target, then swap: OpenCode only picks up *.ts and *.js.
            var tempPath = targetPath + ".fleet-tmp";
            File.Copy(sourceFile.Value, tempPath, overwrite: true);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.InstallNativeFailed(_logger, ex, name);
            return new FleetError("ToolInstaller.InstallNativeFailed", $"Failed to install native tool '{name}': {ex.Message}");
        }

        Log.NativeToolInstalled(_logger, name, targetPath);
        await RecyclePooledInstancesAsync(cancellationToken).ConfigureAwait(false);
        return targetPath;
    }

    public async Task<Result<string>> InstallMcpAsync(
        string name,
        string command,
        IReadOnlyList<string>? args,
        IReadOnlyDictionary<string, string>? env,
        InstallTarget target,
        CancellationToken cancellationToken = default)
    {
        if (ValidateName(name) is { } invalid)
            return invalid;

        if (string.IsNullOrWhiteSpace(command))
            return FleetError.ValidationError("ToolInstaller", "MCP tool command cannot be empty.");

        var configPath = ResolveConfigFile(_paths.OpenCodeConfigDirectory(target));
        var server = BuildMcpServer(command, args, env);

        try
        {
            var text = File.Exists(configPath)
                ? await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false)
                : NewConfig;

            var existing = ReadConfig(text)?["mcp"]?[name];
            if (existing is not null && !JsonNode.DeepEquals(existing, server))
                return Conflict($"An MCP server named '{name}' is already in {configPath}. Remove it there, or pick another name.");

            await WriteAtomicallyAsync(configPath, JsoncEditor.SetProperty(text, ["mcp", name], server), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Log.InstallMcpFailed(_logger, ex, name);
            return new FleetError("ToolInstaller.InstallMcpFailed", $"Failed to add MCP server '{name}' to {configPath}: {ex.Message}");
        }

        Log.McpToolInstalled(_logger, name, configPath);
        await RecyclePooledInstancesAsync(cancellationToken).ConfigureAwait(false);
        return configPath;
    }

    public async Task<Result<Unit>> UninstallAsync(ToolManifestEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var target = InstallTarget.From(entry.Scope, entry.ProjectPath);
        var result = entry.ToolType == ToolType.Mcp
            ? await UninstallMcpAsync(entry, target, cancellationToken).ConfigureAwait(false)
            : UninstallNative(entry, target);

        if (result.IsSuccess)
            await RecyclePooledInstancesAsync(cancellationToken).ConfigureAwait(false);

        return result;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private Result<Unit> UninstallNative(ToolManifestEntry entry, InstallTarget target)
    {
        if (entry.InstalledPath is null)
            return Unit.Value;

        var toolsDir = _paths.OpenCodeToolsDirectory(target);
        var expected = ToolExtensions.Any(ext => InstalledFiles.SamePath(entry.InstalledPath, Path.Combine(toolsDir, entry.Name + ext)));
        if (!expected)
            return FleetError.ValidationError("InstalledPath", $"Not deleting {entry.InstalledPath}: it isn't '{entry.Name}' in {toolsDir}.");

        try
        {
            if (InstalledFiles.Exists(entry.InstalledPath))
                InstalledFiles.Delete(entry.InstalledPath);
            return Unit.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FleetError("ToolInstaller.UninstallFailed", $"Failed to delete {entry.InstalledPath}: {ex.Message}");
        }
    }

    private async Task<Result<Unit>> UninstallMcpAsync(ToolManifestEntry entry, InstallTarget target, CancellationToken cancellationToken)
    {
        var configDir = _paths.OpenCodeConfigDirectory(target);

        // Entries from older Fleet versions have no path; they wrote the legacy key into the global config.
        var (configPath, key) = entry.InstalledPath is null
            ? (ResolveConfigFile(configDir), LegacyMcpKey)
            : (entry.InstalledPath, "mcp");

        if (!ConfigFileNames.Any(fileName => InstalledFiles.SamePath(configPath, Path.Combine(configDir, fileName))))
            return FleetError.ValidationError("InstalledPath", $"Not editing {configPath}: it isn't OpenCode's config in {configDir}.");

        if (!File.Exists(configPath))
            return Unit.Value;

        try
        {
            var text = await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
            var (updated, removed) = JsoncEditor.RemoveProperty(text, [key, entry.Name]);
            if (!removed)
                return Unit.Value;

            // OpenCode doesn't know the legacy key; drop it with its last server.
            if (key == LegacyMcpKey && ReadConfig(updated)?[LegacyMcpKey] is JsonObject { Count: 0 })
                updated = JsoncEditor.RemoveProperty(updated, [LegacyMcpKey]).Text;

            await WriteAtomicallyAsync(configPath, updated, cancellationToken).ConfigureAwait(false);
            return Unit.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new FleetError("ToolInstaller.UninstallFailed", $"Failed to remove MCP server '{entry.Name}' from {configPath}: {ex.Message}");
        }
    }

    /// <summary>The tool file to copy: the file itself, or &lt;name&gt;.ts/.js (or the only .ts/.js) in a folder.</summary>
    private static Result<string> ResolveToolFile(string name, string sourcePath)
    {
        if (File.Exists(sourcePath))
        {
            return IsToolFile(sourcePath)
                ? Result.Success(sourcePath)
                : FleetError.ValidationError("ToolSource", $"A native tool is a .ts or .js file: {sourcePath}");
        }

        if (!Directory.Exists(sourcePath))
            return FleetError.NotFoundFor("ToolSource", sourcePath);

        var named = ToolExtensions.Select(ext => Path.Combine(sourcePath, name + ext)).FirstOrDefault(File.Exists);
        if (named is not null)
            return Result.Success(named);

        var candidates = Directory.EnumerateFiles(sourcePath).Where(IsToolFile).ToList();
        return candidates.Count == 1
            ? Result.Success(candidates[0])
            : FleetError.ValidationError("ToolSource", $"Found no {name}.ts or {name}.js in {sourcePath}.");
    }

    private static bool IsToolFile(string path) =>
        ToolExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The config file to edit: an existing opencode.json or opencode.jsonc, else a new opencode.json.</summary>
    private static string ResolveConfigFile(string configDir) =>
        ConfigFileNames.Select(fileName => Path.Combine(configDir, fileName)).FirstOrDefault(File.Exists)
        ?? Path.Combine(configDir, ConfigFileNames[0]);

    /// <summary>A local MCP server as OpenCode expects it: the command and its arguments in one array.</summary>
    internal static JsonObject BuildMcpServer(string command, IReadOnlyList<string>? args, IReadOnlyDictionary<string, string>? env)
    {
        var commandArray = new JsonArray { (JsonNode)command };
        foreach (var arg in args ?? [])
            commandArray.Add((JsonNode)arg);

        var server = new JsonObject
        {
            ["type"] = "local",
            ["command"] = commandArray
        };

        if (env is { Count: > 0 })
        {
            var environment = new JsonObject();
            foreach (var (key, value) in env)
                environment[key] = value;
            server["environment"] = environment;
        }

        server["enabled"] = true;
        return server;
    }

    private static JsonNode? ReadConfig(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text, documentOptions: ConfigReadOptions);

    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".fleet-tmp";
        await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }

    private static FleetError? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
        {
            return FleetError.ValidationError("ToolName", $"Invalid tool name: '{name}'");
        }

        return null;
    }

    private static FleetError Conflict(string message) => new(FleetError.Conflict.Code, message);

    /// <summary>Recycles idle pooled OpenCode instances so they load the changed tools. Best-effort.</summary>
    private async Task RecyclePooledInstancesAsync(CancellationToken cancellationToken)
    {
        if (_poolRecycler is null)
            return;

        try
        {
            await _poolRecycler.RecycleIdleInstancesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.PoolRecycleFailed(_logger, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Native tool '{ToolName}' installed to {TargetPath}")]
        public static partial void NativeToolInstalled(ILogger logger, string toolName, string targetPath);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to install native tool '{ToolName}'")]
        public static partial void InstallNativeFailed(ILogger logger, Exception ex, string toolName);

        [LoggerMessage(Level = LogLevel.Information, Message = "MCP tool '{ToolName}' registered in {ConfigPath}")]
        public static partial void McpToolInstalled(ILogger logger, string toolName, string configPath);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to install MCP tool '{ToolName}'")]
        public static partial void InstallMcpFailed(ILogger logger, Exception ex, string toolName);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to recycle pooled harness instances after a tool change.")]
        public static partial void PoolRecycleFailed(ILogger logger, Exception ex);
    }
}
