namespace NuCode.Lsp;

/// <summary>
/// Maps file extensions to LSP language identifiers.
/// </summary>
internal static class LspLanguageId
{
    private static readonly Dictionary<string, string> s_map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".ts"] = "typescript",
        [".tsx"] = "typescriptreact",
        [".js"] = "javascript",
        [".jsx"] = "javascriptreact",
        [".mjs"] = "javascript",
        [".cjs"] = "javascript",
        [".py"] = "python",
        [".rs"] = "rust",
        [".go"] = "go",
        [".java"] = "java",
        [".rb"] = "ruby",
        [".cpp"] = "cpp",
        [".cc"] = "cpp",
        [".cxx"] = "cpp",
        [".c"] = "c",
        [".h"] = "c",
        [".hpp"] = "cpp",
        [".hxx"] = "cpp",
        [".json"] = "json",
        [".jsonc"] = "jsonc",
        [".yaml"] = "yaml",
        [".yml"] = "yaml",
        [".md"] = "markdown",
        [".html"] = "html",
        [".htm"] = "html",
        [".css"] = "css",
        [".scss"] = "scss",
        [".less"] = "less",
        [".xml"] = "xml",
        [".sh"] = "shellscript",
        [".bash"] = "shellscript",
        [".zsh"] = "shellscript",
        [".ps1"] = "powershell",
        [".psm1"] = "powershell",
        [".kt"] = "kotlin",
        [".kts"] = "kotlin",
        [".swift"] = "swift",
        [".dart"] = "dart",
        [".lua"] = "lua",
        [".r"] = "r",
        [".php"] = "php",
        [".sql"] = "sql",
        [".fs"] = "fsharp",
        [".fsx"] = "fsharp",
        [".fsi"] = "fsharp",
        [".vb"] = "vb",
        [".tf"] = "terraform",
        [".toml"] = "toml",
        [".ini"] = "ini",
        [".dockerfile"] = "dockerfile",
        [".graphql"] = "graphql",
        [".gql"] = "graphql",
    };

    /// <summary>
    /// Gets the LSP language identifier for the given file extension.
    /// </summary>
    /// <param name="fileExtension">The file extension (with or without leading dot).</param>
    /// <returns>The LSP language identifier, or the extension without the dot as a fallback.</returns>
    public static string GetLanguageId(string fileExtension)
    {
        if (string.IsNullOrEmpty(fileExtension)) return "plaintext";

        // Normalize: ensure leading dot
        var ext = fileExtension.StartsWith('.') ? fileExtension : "." + fileExtension;

        return s_map.TryGetValue(ext, out var id) ? id : ext.TrimStart('.');
    }
}
