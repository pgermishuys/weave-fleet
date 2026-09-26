using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class DirectoryEndpoints
{
    public static IEndpointRouteBuilder MapDirectoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Directories");

        // GET /api/directories?path= — lists subdirectories at the given path
        // Returns workspace roots if no path provided
        group.MapGet("/directories", async (
            string? path,
            bool? unconstrained,
            DirectoryService directoryService,
            CancellationToken ct) =>
        {
            var result = unconstrained == true
                ? await directoryService.ListDirectoryUnconstrainedAsync(path, ct)
                : await directoryService.ListDirectoryAsync(path, ct);
            return Results.Ok(new DirectoryListingResponse(
                Entries: result.Entries.Select(e => new DirectoryEntryResponse(
                    Name: e.Name,
                    Path: e.FullPath,
                    IsGitRepo: e.IsGitRepo,
                    IsRoot: e.IsRoot)).ToList(),
                CurrentPath: result.CurrentPath,
                ParentPath: result.ParentPath,
                Roots: result.Roots));
        })
        .WithName("GetDirectories");

        // GET /api/directories/inspect?path= — whether a folder exists, is a git repository,
        // and is inside the workspace roots
        group.MapGet("/directories/inspect", async (
            string path,
            DirectoryService directoryService,
            CancellationToken ct) =>
        {
            var inspection = await directoryService.InspectFolderAsync(path, ct);
            return Results.Ok(new FolderInspectionResponse(
                inspection.Path,
                inspection.Exists,
                inspection.IsGitRepo,
                inspection.IsWithinRoots));
        })
        .WithName("InspectDirectory");

        // POST /api/directories — creates a folder to start a session in, optionally a git repository
        // with an empty first commit. One outside the workspace roots is added to them.
        group.MapPost("/directories", async (
            CreateFolderRequest req,
            NewFolderService newFolders,
            CancellationToken ct) =>
        {
            var result = await newFolders.CreateAsync(req.Path, req.Git, ct);
            return result.IsSuccess
                ? Results.Ok(ToResponse(result.Value))
                : result.Error.ToErrorResult();
        })
        .WithName("CreateDirectory");

        // POST /api/directories/clone — clones a repository into a new folder. A request Fleet refuses
        // is an ordinary error response; once git is running, the response is newline-delimited JSON:
        // "progress" lines, then one "done" or "error" line.
        group.MapPost("/directories/clone", async (
            CloneFolderRequest req,
            NewFolderService newFolders,
            HttpContext context,
            CancellationToken ct) =>
        {
            var channel = Channel.CreateUnbounded<CloneProgress>(new UnboundedChannelOptions { SingleReader = true });
            var clone = CloneThenCompleteAsync();

            async Task<WeaveFleet.Domain.Common.Result<NewFolder>> CloneThenCompleteAsync()
            {
                try
                {
                    return await newFolders.CloneAsync(
                        req.Repository, req.Path, new ChannelProgress(channel.Writer), ct);
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            }

            try
            {
                // The channel completes when the clone ends, which the same token also stops.
                await foreach (var step in channel.Reader.ReadAllAsync(CancellationToken.None))
                    await WriteCloneLineAsync(context, new CloneStreamLine("progress", step.Phase, step.Percent, null, null), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The page went away: git is stopped and its half-made folder removed.
                try { await clone; }
                catch (OperationCanceledException) { }
                return Results.Empty;
            }

            var result = await clone;
            if (!context.Response.HasStarted && result.IsFailure)
                return result.Error.ToErrorResult();

            await WriteCloneLineAsync(context, result.IsSuccess
                ? new CloneStreamLine("done", null, null, ToResponse(result.Value), null)
                : new CloneStreamLine("error", null, null, null, result.Error.Description), ct);
            return Results.Empty;
        })
        .WithName("CloneDirectory");

        return app;
    }

    private static NewFolderResponse ToResponse(NewFolder folder) =>
        new(folder.Path, folder.IsGitRepo, folder.AddedToFleet, folder.Warning);

    private static async Task WriteCloneLineAsync(HttpContext context, CloneStreamLine line, CancellationToken ct)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/x-ndjson";
            context.Response.Headers.CacheControl = "no-cache";
        }

        var json = JsonSerializer.Serialize(line, ApiJsonContext.Default.CloneStreamLine);
        await context.Response.WriteAsync(json + "\n", Encoding.UTF8, ct);
        await context.Response.Body.FlushAsync(ct);
    }

    /// <summary>Hands git's progress to the request, which writes it; git reports from its own thread.</summary>
    private sealed class ChannelProgress(ChannelWriter<CloneProgress> writer) : IProgress<CloneProgress>
    {
        public void Report(CloneProgress value) => writer.TryWrite(value);
    }
}

internal sealed record CreateFolderRequest(string Path, bool Git);

internal sealed record CloneFolderRequest(string Repository, string Path);

public sealed record NewFolderResponse(string Path, bool IsGitRepo, bool AddedToFleet, string? Warning);

/// <summary>One line of a clone's response: <c>progress</c>, then <c>done</c> or <c>error</c>.</summary>
public sealed record CloneStreamLine(string Type, string? Phase, int? Percent, NewFolderResponse? Folder, string? Error);

public sealed record DirectoryListingResponse(
    IReadOnlyList<DirectoryEntryResponse> Entries,
    string? CurrentPath,
    string? ParentPath,
    IReadOnlyList<string> Roots);

public sealed record FolderInspectionResponse(
    string Path,
    bool Exists,
    bool IsGitRepo,
    bool IsWithinRoots);

public sealed record DirectoryEntryResponse(
    string Name,
    string Path,
    bool IsGitRepo,
    bool IsRoot);
#pragma warning restore IL2026
