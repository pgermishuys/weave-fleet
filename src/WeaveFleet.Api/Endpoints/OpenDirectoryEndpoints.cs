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

        // POST /api/open-directory — opens a directory in the OS file manager
        group.MapPost("/open-directory", async (OpenDirectoryRequest req, WorkspaceRootService workspaceRootService) =>
        {
            if (string.IsNullOrWhiteSpace(req.Path) || !Directory.Exists(req.Path))
                return Results.BadRequest(new ErrorResponse("Directory does not exist."));

            var normalised = Path.GetFullPath(req.Path);
            var allowedRoots = await workspaceRootService.GetAllowedRootsAsync();
            if (!IsUnderAllowedRoot(normalised, allowedRoots))
                return Results.BadRequest(new ErrorResponse("Path is outside allowed workspace roots."));

            try
            {
                OpenInFileManager(normalised);
                return Results.NoContent();
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to open directory: {ex.Message}");
            }
        })
        .WithName("OpenDirectory");

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

internal sealed record OpenDirectoryRequest(string Path);
#pragma warning restore IL2026
