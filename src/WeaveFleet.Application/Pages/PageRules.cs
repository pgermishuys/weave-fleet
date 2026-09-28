using System.Text.RegularExpressions;

namespace WeaveFleet.Application.Pages;

/// <summary>
/// What <c>fleet_page_show</c> takes and what it leaves to the other tools. A page is an HTML file the agent
/// wrote, with the web files in its folder. A project's page needs the project's server, so it goes to
/// <c>fleet_app_start</c>; a plain file server started through <c>fleet_app_start</c> goes the other way.
/// </summary>
public static partial class PageRules
{
    /// <summary>The most files one page copies.</summary>
    public const int MaxFiles = 500;

    /// <summary>The most bytes one page copies.</summary>
    public const long MaxBytes = 25L * 1024 * 1024;

    /// <summary>How many folder entries Fleet looks at before it gives up on a folder, web files or not.</summary>
    public const int MaxEntriesVisited = 5_000;

    /// <summary>How much of the page Fleet reads to check its links.</summary>
    public const int MaxEntryBytesRead = 2 * 1024 * 1024;

    /// <summary>The files a page folder serves. Anything else in the folder stays behind.</summary>
    public static readonly IReadOnlySet<string> WebExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".css", ".js", ".mjs", ".json", ".map", ".svg", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif",
        ".ico", ".woff", ".woff2", ".ttf", ".otf", ".mp4", ".webm", ".mp3", ".wav", ".txt", ".csv",
    };

    private static readonly string[] ProjectFiles =
    [
        "package.json", "deno.json", "deno.jsonc", "Cargo.toml", "go.mod", "pyproject.toml", "Gemfile", "composer.json",
    ];

    private static readonly string[] ProjectFileExtensions = [".csproj", ".fsproj", ".sln", ".slnx"];

    public static bool IsPageFile(string path)
        => Path.GetExtension(path) is { } extension
           && (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase));

    public static bool IsWebFile(string name) => WebExtensions.Contains(Path.GetExtension(name));

    /// <summary>The project file in <paramref name="folder"/> (not its parents), or null when it holds none.</summary>
    public static string? FindProjectFile(string folder)
    {
        foreach (var name in ProjectFiles)
        {
            if (File.Exists(Path.Combine(folder, name)))
                return name;
        }

        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(file);
            if (ProjectFileExtensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                return name;
        }

        return null;
    }

    /// <summary>A script the page loads from source that only a build or a dev server turns into JavaScript, e.g. <c>/src/main.ts</c>.</summary>
    public static string? FindSourceScript(string html)
    {
        var match = SourceScript().Match(html);
        return match.Success ? match.Groups["src"].Value : null;
    }

    /// <summary>
    /// Links in the page that can't load from its copy: paths from the root (<c>/styles.css</c>) resolve against
    /// Fleet, and paths out of the folder (<c>../shared.css</c>) weren't copied. At most five, each once.
    /// </summary>
    public static IReadOnlyList<string> FindBrokenLinks(string html)
        => [.. LocalLink().Matches(html)
            .Select(match => match.Groups["link"].Value)
            .Where(link => (link.StartsWith('/') && !link.StartsWith("//", StringComparison.Ordinal)) || link.StartsWith("../", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Take(5)];

    /// <summary>
    /// True when <paramref name="command"/> only serves files: <c>python -m http.server</c>, <c>npx serve</c>,
    /// <c>http-server</c>, <c>live-server</c>. Fleet serves files itself (<c>fleet_page_show</c>).
    /// </summary>
    public static bool IsStaticFileServer(string command)
        => PythonHttpServer().IsMatch(command) || PackageFileServer().IsMatch(command);

    [GeneratedRegex("""<script\b[^>]*\bsrc\s*=\s*["']?(?<src>[^"'\s>?#]+\.(?:tsx|ts|jsx|vue|svelte))(?=[?#"'\s>]|$)""", RegexOptions.IgnoreCase)]
    private static partial Regex SourceScript();

    [GeneratedRegex("""\b(?:src|href)\s*=\s*["'](?<link>[^"'\s]+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex LocalLink();

    // A command segment starts the command or follows ; & | or (, as in `cd site && python3 -m http.server`.
    [GeneratedRegex(@"(?:^|[;&|(]\s*)(?:python3?|py)(?:\.exe)?\s+(?:-\S+\s+)*-m\s+http\.server(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex PythonHttpServer();

    [GeneratedRegex(@"(?:^|[;&|(]\s*)(?:(?:npx|bunx|pnpx)\s+|(?:pnpm|yarn)\s+dlx\s+|bun\s+x\s+)?(?:-{1,2}\S+\s+)*(?:serve|http-server|live-server)(?:@\S+)?(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex PackageFileServer();
}
