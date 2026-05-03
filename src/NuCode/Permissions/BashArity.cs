using System.Collections.Frozen;

namespace NuCode.Permissions;

/// <summary>
/// Maps command prefixes to arity (number of tokens that form the command identity for permission patterns).
/// Used by the bash tool to determine what pattern to check permissions against.
/// </summary>
internal static class BashArity
{
    // Dictionary mapping command prefix → number of tokens that define the command
    private static readonly FrozenDictionary<string, int> s_arity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        // Arity 1: basic shell commands
        ["cat"] = 1, ["cd"] = 1, ["chmod"] = 1, ["chown"] = 1,
        ["cp"] = 1, ["echo"] = 1, ["find"] = 1, ["grep"] = 1,
        ["head"] = 1, ["kill"] = 1, ["less"] = 1, ["ln"] = 1,
        ["ls"] = 1, ["mkdir"] = 1, ["mv"] = 1, ["pwd"] = 1,
        ["rm"] = 1, ["rmdir"] = 1, ["sed"] = 1, ["tail"] = 1,
        ["tar"] = 1, ["touch"] = 1, ["wc"] = 1, ["which"] = 1,
        ["whoami"] = 1, ["xargs"] = 1,
        // Windows equivalents
        ["dir"] = 1, ["copy"] = 1, ["del"] = 1, ["move"] = 1,
        ["type"] = 1, ["where"] = 1,

        // Arity 2: package managers, container tools, VCS, cloud CLIs
        ["apt"] = 2, ["apt-get"] = 2, ["brew"] = 2,
        ["bun"] = 2, ["cargo"] = 2, ["cmake"] = 2,
        ["composer"] = 2, ["conda"] = 2, ["deno"] = 2,
        ["docker"] = 2, ["dotnet"] = 2, ["gem"] = 2,
        ["gh"] = 2, ["git"] = 2, ["go"] = 2,
        ["gradle"] = 2, ["kubectl"] = 2, ["make"] = 2,
        ["maven"] = 2, ["mix"] = 2, ["npm"] = 2,
        ["npx"] = 2, ["nvm"] = 2, ["pip"] = 2,
        ["pip3"] = 2, ["pnpm"] = 2, ["poetry"] = 2,
        ["podman"] = 2, ["rustup"] = 2, ["swift"] = 2,
        ["terraform"] = 2, ["uv"] = 2, ["yarn"] = 2,
        // Windows package managers
        ["choco"] = 2, ["scoop"] = 2, ["winget"] = 2,
        ["nuget"] = 2,

        // Arity 3: multi-level subcommands
        ["aws"] = 3, ["bun run"] = 3, ["cargo build"] = 3,
        ["deno task"] = 3, ["docker compose"] = 3,
        ["dotnet build"] = 3, ["dotnet run"] = 3, ["dotnet test"] = 3,
        ["dotnet new"] = 3, ["dotnet add"] = 3, ["dotnet remove"] = 3,
        ["gcloud"] = 3, ["git config"] = 3, ["git remote"] = 3,
        ["git submodule"] = 3, ["go build"] = 3, ["go run"] = 3,
        ["go test"] = 3, ["kubectl apply"] = 3, ["kubectl get"] = 3,
        ["npm init"] = 3, ["npm run"] = 3, ["npm exec"] = 3,
        ["pnpm run"] = 3, ["poetry run"] = 3,
        ["terraform plan"] = 3, ["terraform apply"] = 3,
        ["yarn run"] = 3,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts the command prefix tokens based on arity rules.
    /// Used to build permission patterns like "git commit" from "git commit -m 'msg'".
    /// </summary>
    /// <param name="tokens">The command split into tokens.</param>
    /// <returns>The prefix tokens that identify the command.</returns>
    public static string[] GetPrefix(string[] tokens)
    {
        if (tokens.Length == 0)
        {
            return [];
        }

        // Try longest prefix match first
        for (var len = tokens.Length; len > 0; len--)
        {
            var prefix = string.Join(' ', tokens, 0, len);
            if (s_arity.TryGetValue(prefix, out var arity))
            {
                return tokens.Length >= arity ? tokens[..arity] : tokens;
            }
        }

        // Default to first token only
        return [tokens[0]];
    }

    /// <summary>
    /// Builds a permission pattern from command tokens.
    /// Returns the command prefix joined with spaces, plus a trailing " *" wildcard.
    /// </summary>
    /// <param name="command">The full command string.</param>
    /// <returns>The permission pattern (e.g., "git commit *").</returns>
    public static string BuildPattern(string command)
    {
        var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var prefix = GetPrefix(tokens);
        return prefix.Length > 0
            ? string.Join(' ', prefix) + " *"
            : "*";
    }
}
