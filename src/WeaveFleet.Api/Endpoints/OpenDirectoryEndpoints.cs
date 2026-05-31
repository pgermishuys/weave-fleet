using System.Diagnostics;
using WeaveFleet.Api;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class OpenDirectoryEndpoints
{
    public static IEndpointRouteBuilder MapOpenDirectoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Directories");

        // POST /api/open-directory — open a workspace directory in an editor, terminal, or file explorer
        group.MapPost("/open-directory", async (OpenDirectoryRequest req, WorkspaceRootService workspaceRootService) =>
        {
            var directory = req.Directory ?? req.Path;

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return Results.BadRequest(new ErrorResponse("Directory does not exist."));

            var normalised = Path.GetFullPath(directory);
            var allowedRoots = await workspaceRootService.GetAllowedRootsAsync();
            if (!IsUnderAllowedRoot(normalised, allowedRoots))
                return Results.BadRequest(new ErrorResponse("Path is outside allowed workspace roots."));

            try
            {
                var tool = req.Tool;

                if (!string.IsNullOrWhiteSpace(tool))
                {
                    // Use tool registry to spawn the right program
                    var psi = ToolRegistry.GetSpawnInfo(tool, normalised);
                    if (psi is null)
                        return Results.BadRequest(new ErrorResponse($"Tool '{tool}' is not available on this platform."));

                    var proc = Process.Start(psi);
                    proc?.Dispose();
                }
                else
                {
                    // Legacy: open in file manager
                    OpenInFileManager(normalised);
                }

                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to open directory: {ex.Message}");
            }
        })
        .WithName("OpenDirectory");

        // POST /api/open-file — open a specific file in an editor
        group.MapPost("/open-file", async (OpenFileRequest req, WorkspaceRootService workspaceRootService) =>
        {
            var filePath = req.FilePath;

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return Results.BadRequest(new ErrorResponse("File does not exist."));

            var normalised = Path.GetFullPath(filePath);
            var allowedRoots = await workspaceRootService.GetAllowedRootsAsync();
            if (!IsUnderAllowedRoot(normalised, allowedRoots))
                return Results.BadRequest(new ErrorResponse("Path is outside allowed workspace roots."));

            try
            {
                var psi = ToolRegistry.GetSpawnInfo(req.Tool, normalised);
                if (psi is null)
                    return Results.BadRequest(new ErrorResponse($"Tool '{req.Tool}' is not available on this platform."));

                var proc = Process.Start(psi);
                proc?.Dispose();

                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to open file: {ex.Message}");
            }
        })
        .WithName("OpenFile");

        return app;
    }

    internal static bool IsUnderAllowedRoot(string path, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            var normRoot = Path.GetFullPath(root);
            if (path.Equals(normRoot, StringComparison.OrdinalIgnoreCase))
                return true;
            var rootWithSep = normRoot.EndsWith(Path.DirectorySeparatorChar)
                ? normRoot
                : normRoot + Path.DirectorySeparatorChar;
            if (path.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void OpenInFileManager(string path)
    {
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
            psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        else if (OperatingSystem.IsMacOS())
            psi = new ProcessStartInfo("open") { UseShellExecute = false };
        else
            psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };

        psi.ArgumentList.Add(path);
        Process.Start(psi);
    }
}

internal sealed record OpenDirectoryRequest(string? Directory = null, string? Tool = null, string? Path = null);
internal sealed record OpenFileRequest(string FilePath, string Tool);
#pragma warning restore IL2026
