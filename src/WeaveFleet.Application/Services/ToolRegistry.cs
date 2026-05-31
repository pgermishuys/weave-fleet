using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WeaveFleet.Application.Services;

public sealed record ToolDefinition(
    string Id,
    string Label,
    string IconName,
    string Category, // "editor" | "terminal" | "explorer"
    IReadOnlyDictionary<string, PlatformCommand> Platforms,
    bool AlwaysAvailable = false,
    IReadOnlyDictionary<string, string[]>? DetectBinaries = null);

public sealed record PlatformCommand(
    string Command,
    Func<string, string[]> Args,
    bool Shell = false,
    bool CwdIsDirectory = false);

public sealed record ResolvedTool(string Id, string Label, string IconName, string Category);

public static class ToolRegistry
{
    private static readonly PlatformID CurrentPlatform = Environment.OSVersion.Platform;

    public static string PlatformKey =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win32"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "darwin"
        : "linux";

    public static readonly IReadOnlyList<ToolDefinition> BuiltinTools = BuildTools();

    private static readonly Dictionary<string, ToolDefinition> ToolIndex =
        BuiltinTools.ToDictionary(t => t.Id);

    public static ToolDefinition? GetById(string id) =>
        ToolIndex.GetValueOrDefault(id);

    private static List<ToolDefinition> BuildTools()
    {
        return
        [
            // ── Editors ──
            MakeEditor("vscode", "VS Code", "code-2",
                win32: ("code", dir => [dir], shell: true),
                darwin: ("open", dir => ["-a", "Visual Studio Code", dir]),
                linux: ("code", dir => [dir]),
                detectWin: ["code"], detectLinux: ["code"]),

            MakeEditor("vscode-insiders", "VS Code Insiders", "code-2",
                win32: ("code-insiders", dir => [dir], shell: true),
                darwin: ("open", dir => ["-a", "Visual Studio Code - Insiders", dir]),
                linux: ("code-insiders", dir => [dir]),
                detectWin: ["code-insiders"], detectLinux: ["code-insiders"]),

            MakeEditor("cursor", "Cursor", "mouse-pointer-2",
                win32: ("cursor", dir => [dir], shell: true),
                darwin: ("open", dir => ["-a", "Cursor", dir]),
                linux: ("cursor", dir => [dir]),
                detectWin: ["cursor"], detectLinux: ["cursor"]),

            MakeEditor("windsurf", "Windsurf", "wind",
                win32: ("windsurf", dir => [dir], shell: true),
                darwin: ("open", dir => ["-a", "Windsurf", dir]),
                linux: ("windsurf", dir => [dir]),
                detectWin: ["windsurf"], detectLinux: ["windsurf"]),

            MakeEditor("zed", "Zed", "pen-tool",
                darwin: ("open", dir => ["-a", "Zed", dir]),
                linux: ("zed", dir => [dir]),
                detectLinux: ["zed"]),

            MakeEditor("sublime", "Sublime Text", "braces",
                win32: ("subl", dir => [dir], shell: true),
                darwin: ("open", dir => ["-a", "Sublime Text", dir]),
                linux: ("subl", dir => [dir]),
                detectWin: ["subl"], detectLinux: ["subl"]),

            MakeEditor("intellij", "IntelliJ IDEA", "app-window",
                win32: ("idea", dir => [dir], shell: true),
                darwin: ("open", dir => ["-a", "IntelliJ IDEA", dir]),
                linux: ("idea", dir => [dir]),
                detectWin: ["idea", "idea64"], detectLinux: ["idea"]),

            MakeEditor("webstorm", "WebStorm", "app-window",
                win32: ("webstorm", dir => [dir], shell: true),
                darwin: ("open", dir => ["-a", "WebStorm", dir]),
                linux: ("webstorm", dir => [dir]),
                detectWin: ["webstorm", "webstorm64"], detectLinux: ["webstorm"]),

            MakeEditor("rider", "Rider", "app-window",
                win32: ("rider", dir => [dir], shell: true),
                darwin: ("open", dir => ["-a", "Rider", dir]),
                linux: ("rider", dir => [dir]),
                detectWin: ["rider", "rider64"], detectLinux: ["rider"]),

            MakeEditor("goland", "GoLand", "app-window",
                win32: ("goland", dir => [dir], shell: true),
                darwin: ("open", dir => ["-a", "GoLand", dir]),
                linux: ("goland", dir => [dir]),
                detectWin: ["goland", "goland64"], detectLinux: ["goland"]),

            MakeEditor("pycharm", "PyCharm", "app-window",
                win32: ("pycharm", dir => [dir], shell: true),
                darwin: ("open", dir => ["-a", "PyCharm", dir]),
                linux: ("pycharm", dir => [dir]),
                detectWin: ["pycharm", "pycharm64"], detectLinux: ["pycharm"]),

            MakeEditor("rustrover", "RustRover", "app-window",
                win32: ("rustrover", dir => [dir], shell: true),
                darwin: ("open", dir => ["-a", "RustRover", dir]),
                linux: ("rustrover", dir => [dir]),
                detectWin: ["rustrover", "rustrover64"], detectLinux: ["rustrover"]),

            MakeEditor("fleet-jb", "Fleet (JetBrains)", "app-window",
                win32: ("fleet", dir => [dir], shell: true),
                darwin: ("open", dir => ["-a", "Fleet", dir]),
                linux: ("fleet", dir => [dir]),
                detectWin: ["fleet"], detectLinux: ["fleet"]),

            MakeEditor("visual-studio", "Visual Studio", "app-window",
                win32: ("devenv", path => [path], shell: true),
                detectWin: ["devenv"]),

            MakeEditor("xcode", "Xcode", "app-window",
                darwin: ("open", path => ["-a", "Xcode", path]),
                detectDarwin: ["xcodebuild"]),

            MakeEditor("clion", "CLion", "app-window",
                win32: ("clion", dir => [dir], shell: true),
                darwin: ("open", dir => ["-a", "CLion", dir]),
                linux: ("clion", dir => [dir]),
                detectWin: ["clion", "clion64"], detectLinux: ["clion"]),

            MakeEditor("android-studio", "Android Studio", "app-window",
                win32: ("studio", dir => [dir], shell: true),
                darwin: ("open", dir => ["-a", "Android Studio", dir]),
                linux: ("studio", dir => [dir]),
                detectWin: ["studio"], detectLinux: ["studio"]),

            // ── Terminals ──
            new("terminal", "System Terminal", "terminal", "terminal",
                new Dictionary<string, PlatformCommand>
                {
                    ["win32"] = new("cmd", _ => ["/c", "start", "cmd", "/K"], Shell: true, CwdIsDirectory: true),
                    ["darwin"] = new("open", _ => ["-a", "Terminal", "."], CwdIsDirectory: true),
                    ["linux"] = new("x-terminal-emulator", _ => [], CwdIsDirectory: true),
                },
                AlwaysAvailable: true),

            new("wt", "Windows Terminal", "square-terminal", "terminal",
                new Dictionary<string, PlatformCommand>
                {
                    ["win32"] = new("wt", dir => ["-d", dir], Shell: true),
                },
                DetectBinaries: new Dictionary<string, string[]> { ["win32"] = ["wt"] }),

            new("iterm2", "iTerm2", "square-terminal", "terminal",
                new Dictionary<string, PlatformCommand>
                {
                    ["darwin"] = new("open", dir => ["-a", "iTerm", dir]),
                }),

            new("alacritty", "Alacritty", "square-terminal", "terminal",
                new Dictionary<string, PlatformCommand>
                {
                    ["win32"] = new("alacritty", dir => ["--working-directory", dir], Shell: true),
                    ["darwin"] = new("alacritty", dir => ["--working-directory", dir]),
                    ["linux"] = new("alacritty", dir => ["--working-directory", dir]),
                },
                DetectBinaries: new Dictionary<string, string[]>
                {
                    ["win32"] = ["alacritty"], ["darwin"] = ["alacritty"], ["linux"] = ["alacritty"],
                }),

            // ── File Explorers ──
            new("explorer", "File Explorer", "folder-open", "explorer",
                new Dictionary<string, PlatformCommand>
                {
                    ["win32"] = new("explorer", dir => [dir]),
                    ["darwin"] = new("open", dir => [dir]),
                    ["linux"] = new("xdg-open", dir => [dir]),
                },
                AlwaysAvailable: true),
        ];
    }

    private static ToolDefinition MakeEditor(string id, string label, string iconName,
        (string cmd, Func<string, string[]> args, bool shell)? win32 = null,
        (string cmd, Func<string, string[]> args)? darwin = null,
        (string cmd, Func<string, string[]> args)? linux = null,
        string[]? detectWin = null, string[]? detectDarwin = null, string[]? detectLinux = null)
    {
        var platforms = new Dictionary<string, PlatformCommand>();
        if (win32 is var (wCmd, wArgs, wShell))
            platforms["win32"] = new PlatformCommand(wCmd, wArgs, Shell: wShell);
        if (darwin is var (dCmd, dArgs))
            platforms["darwin"] = new PlatformCommand(dCmd, dArgs);
        if (linux is var (lCmd, lArgs))
            platforms["linux"] = new PlatformCommand(lCmd, lArgs);

        Dictionary<string, string[]>? detect = null;
        if (detectWin != null || detectDarwin != null || detectLinux != null)
        {
            detect = new Dictionary<string, string[]>();
            if (detectWin != null) detect["win32"] = detectWin;
            if (detectDarwin != null) detect["darwin"] = detectDarwin;
            if (detectLinux != null) detect["linux"] = detectLinux;
        }

        return new ToolDefinition(id, label, iconName, "editor", platforms, DetectBinaries: detect);
    }

    /// <summary>
    /// Build spawn ProcessStartInfo for a given tool + directory.
    /// </summary>
    public static ProcessStartInfo? GetSpawnInfo(string toolId, string directory)
    {
        var tool = GetById(toolId);
        if (tool is null) return null;

        var platform = PlatformKey;
        if (!tool.Platforms.TryGetValue(platform, out var cmd)) return null;

        var safeDir = Path.GetFullPath(directory);
        var args = cmd.Args(safeDir);

        var psi = new ProcessStartInfo(cmd.Command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        if (cmd.Shell && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // On Windows, wrap in cmd /c for shell-based commands
            psi = new ProcessStartInfo("cmd")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(cmd.Command);
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
        }

        if (cmd.CwdIsDirectory)
            psi.WorkingDirectory = safeDir;

        return psi;
    }
}
