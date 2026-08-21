using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Tools;
using WeaveFleet.Domain.Common;

namespace WeaveFleet.Infrastructure.Tools;

/// <summary>
/// Service for installing tools to the local filesystem or configuration.
/// Handles both native tools (copy .ts files) and MCP tools (write to opencode.json).
/// </summary>
public sealed partial class ToolInstaller : IToolInstaller
{
    private readonly ILogger<ToolInstaller> _logger;
    private readonly string? _baseDirectory;

    public ToolInstaller(ILogger<ToolInstaller> logger, string? baseDirectory = null)
    {
        _logger = logger;
        _baseDirectory = baseDirectory;
    }

    public async Task<Result<string>> InstallNativeAsync(
        string name,
        string sourcePath,
        string userId,
        string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateToolName(name);
            ValidateUserId(userId);
            ValidateWorkspaceId(workspaceId);

            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            {
                return FleetError.NotFoundFor("ToolSource", sourcePath);
            }

            var targetDir = GetNativeToolDirectory(userId, workspaceId);
            Directory.CreateDirectory(targetDir);

            var targetPath = Path.Combine(targetDir, Path.GetFileName(sourcePath));

            // Copy file or directory
            if (File.Exists(sourcePath))
            {
                File.Copy(sourcePath, targetPath, overwrite: true);
                Log.NativeToolInstalled(_logger, name, targetPath);
            }
            else
            {
                CopyDirectory(sourcePath, targetPath);
                Log.NativeToolInstalled(_logger, name, targetPath);
            }

            return Result.Success(targetPath);
        }
        catch (Exception ex)
        {
            Log.InstallNativeFailed(_logger, ex, name);
            return new FleetError("ToolInstaller.InstallNativeFailed", $"Failed to install native tool '{name}': {ex.Message}");
        }
    }

    public async Task<Result<string>> InstallMcpAsync(
        string name,
        string command,
        IReadOnlyList<string>? args,
        IReadOnlyDictionary<string, string>? env,
        string userId,
        string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateToolName(name);
            ValidateUserId(userId);
            ValidateWorkspaceId(workspaceId);

            if (string.IsNullOrWhiteSpace(command))
            {
                return FleetError.ValidationError("ToolInstaller", "MCP tool command cannot be empty.");
            }

            var configPath = GetOpencodeConfigPath(userId, workspaceId);
            var configDir = Path.GetDirectoryName(configPath)!;
            Directory.CreateDirectory(configDir);

            // Read existing config or create new
            JsonObject rootObject;
            if (File.Exists(configPath))
            {
                var existingJson = await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
                rootObject = JsonNode.Parse(existingJson)?.AsObject() ?? new JsonObject();
            }
            else
            {
                rootObject = new JsonObject();
            }

            // Ensure mcpServers object exists
            if (!rootObject.TryGetPropertyValue("mcpServers", out var mcpServersNode) || mcpServersNode is not JsonObject)
            {
                rootObject["mcpServers"] = new JsonObject();
            }

            var mcpServers = rootObject["mcpServers"]!.AsObject();

            // Build the tool entry
            var toolEntry = new JsonObject
            {
                ["command"] = command
            };

            if (args is not null && args.Count > 0)
            {
                var argsArray = new JsonArray();
                foreach (var arg in args)
                {
#pragma warning disable IL2026 // Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code
                    argsArray.Add(JsonValue.Create(arg));
#pragma warning restore IL2026
                }
                toolEntry["args"] = argsArray;
            }

            if (env is not null && env.Count > 0)
            {
                var envObject = new JsonObject();
                foreach (var kvp in env)
                {
                    envObject[kvp.Key] = kvp.Value;
                }
                toolEntry["env"] = envObject;
            }

            // Add or update the tool entry
            mcpServers[name] = toolEntry;

            // Write back atomically
            var tempPath = configPath + ".tmp";
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = rootObject.ToJsonString(options);
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, configPath, overwrite: true);

            Log.McpToolInstalled(_logger, name, configPath);
            return Result.Success(configPath);
        }
        catch (Exception ex)
        {
            Log.InstallMcpFailed(_logger, ex, name);
            return new FleetError("ToolInstaller.InstallMcpFailed", $"Failed to install MCP tool '{name}': {ex.Message}");
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private string GetNativeToolDirectory(string userId, string? workspaceId)
    {
        var baseDir = _baseDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configDir = Path.Combine(baseDir, ".config", "weave-fleet", "tools");

        return workspaceId is null
            ? Path.Combine(configDir, userId)
            : Path.Combine(configDir, userId, workspaceId);
    }

    private string GetOpencodeConfigPath(string userId, string? workspaceId)
    {
        var baseDir = _baseDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configDir = Path.Combine(baseDir, ".config", "opencode");

        return workspaceId is null
            ? Path.Combine(configDir, "opencode.json")
            : Path.Combine(configDir, workspaceId, "opencode.json");
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var targetFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, targetFile, overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var targetSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, targetSubDir);
        }
    }

    private static void ValidateToolName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tool name cannot be empty.", nameof(name));

        if (name.Contains('/', StringComparison.Ordinal) ||
            name.Contains('\\', StringComparison.Ordinal) ||
            name.Contains('\0', StringComparison.Ordinal) ||
            name is "." or "..")
        {
            throw new ArgumentException($"Tool name contains invalid characters: '{name}'", nameof(name));
        }
    }

    private static void ValidateUserId(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("userId cannot be empty.", nameof(userId));

        if (userId.Contains('/', StringComparison.Ordinal) ||
            userId.Contains('\\', StringComparison.Ordinal) ||
            userId.Contains('\0', StringComparison.Ordinal) ||
            userId is "." or "..")
        {
            throw new ArgumentException($"userId contains invalid characters: '{userId}'", nameof(userId));
        }
    }

    private static void ValidateWorkspaceId(string? workspaceId)
    {
        if (workspaceId is null)
            return;

        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("workspaceId cannot be empty when provided.", nameof(workspaceId));

        if (workspaceId.Contains('/', StringComparison.Ordinal) ||
            workspaceId.Contains('\\', StringComparison.Ordinal) ||
            workspaceId.Contains('\0', StringComparison.Ordinal) ||
            workspaceId is "." or "..")
        {
            throw new ArgumentException($"workspaceId contains invalid characters: '{workspaceId}'", nameof(workspaceId));
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
    }
}
