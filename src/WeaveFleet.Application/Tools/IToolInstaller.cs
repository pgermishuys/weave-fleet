using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Skills;
using WeaveFleet.Domain.Tools;

namespace WeaveFleet.Application.Tools;

/// <summary>
/// Installs tools where OpenCode loads them: native tools into its <c>tools</c> folder, MCP servers
/// into the <c>mcp</c> section of <c>opencode.json</c>. Globally or inside one repository.
/// </summary>
public interface IToolInstaller
{
    /// <summary>
    /// Copies a native tool into OpenCode's tools folder as <c>&lt;name&gt;.ts</c> (or <c>.js</c>);
    /// OpenCode names the tool after the file.
    /// </summary>
    /// <param name="name">The unique name of the tool.</param>
    /// <param name="sourcePath">
    /// A <c>.ts</c>/<c>.js</c> file, or a folder holding <c>&lt;name&gt;.ts</c>/<c>.js</c> or exactly one such file.
    /// </param>
    /// <param name="target">Global, or the repository to install into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file written. A conflict when a different file Fleet didn't write is already there.</returns>
    Task<Result<string>> InstallNativeAsync(
        string name,
        string sourcePath,
        InstallTarget target,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a local MCP server to the <c>mcp</c> section of <c>opencode.json</c>, keeping the file's comments.
    /// </summary>
    /// <param name="name">The unique name of the tool.</param>
    /// <param name="command">The command to execute for the MCP tool.</param>
    /// <param name="args">Optional command-line arguments.</param>
    /// <param name="env">Optional environment variables.</param>
    /// <param name="target">Global, or the repository to install into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The config file written. A conflict when a different server with that name is already there.</returns>
    Task<Result<string>> InstallMcpAsync(
        string name,
        string command,
        IReadOnlyList<string>? args,
        IReadOnlyDictionary<string, string>? env,
        InstallTarget target,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes what Fleet installed for this entry: the tool file, or the server in <c>opencode.json</c>.
    /// Succeeds when there's nothing left to delete.
    /// </summary>
    Task<Result<Unit>> UninstallAsync(ToolManifestEntry entry, CancellationToken cancellationToken = default);
}
