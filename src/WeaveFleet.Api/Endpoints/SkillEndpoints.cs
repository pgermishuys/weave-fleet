using WeaveFleet.Api;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// CRUD endpoints for weave skill management (~/.weave/skills/).
/// </summary>
public static class SkillEndpoints
{
    private static readonly string SkillsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave", "skills");

    public static IEndpointRouteBuilder MapSkillEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/skills").WithTags("Skills");

        // GET /api/skills — list installed skills
        group.MapGet("", () =>
        {
            if (!Directory.Exists(SkillsDir))
                return Results.Ok(Array.Empty<SkillListItem>());

            var skills = Directory.EnumerateDirectories(SkillsDir)
                .Select(dir => new SkillListItem(
                    Name: Path.GetFileName(dir),
                    Path: dir,
                    HasPrompt: File.Exists(Path.Combine(dir, "prompt.md"))
                               || File.Exists(Path.Combine(dir, "PROMPT.md"))))
                .ToArray();

            return Results.Ok(skills);
        })
        .WithName("ListSkills");

        // GET /api/skills/{name} — single skill detail
        group.MapGet("/{name}", (string name) =>
        {
            if (!IsValidSkillName(name))
                return Results.BadRequest(new ErrorResponse("Invalid skill name."));

            var dir = Path.Combine(SkillsDir, name);
            if (!IsContainedInSkillsDir(dir))
                return Results.BadRequest(new ErrorResponse("Invalid skill name."));

            if (!Directory.Exists(dir))
                return Results.NotFound(new ErrorResponse($"Skill '{name}' not found."));

            var promptFile = Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            var prompt = promptFile is not null ? File.ReadAllText(promptFile) : null;

            return Results.Ok(new SkillDetailResponse(name, dir, prompt));
        })
        .WithName("GetSkill");

        // POST /api/skills — install skill from a source path
        group.MapPost("", (InstallSkillRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new ErrorResponse("Skill name is required."));

            if (!IsValidSkillName(req.Name))
                return Results.BadRequest(new ErrorResponse("Invalid skill name."));

            var dest = Path.Combine(SkillsDir, req.Name);
            if (!IsContainedInSkillsDir(dest))
                return Results.BadRequest(new ErrorResponse("Invalid skill name."));

            if (Directory.Exists(dest))
                return Results.Conflict(new ErrorResponse($"Skill '{req.Name}' already exists."));

            if (!string.IsNullOrEmpty(req.SourcePath))
            {
                if (!Directory.Exists(req.SourcePath))
                    return Results.BadRequest(new ErrorResponse("Source path does not exist."));

                CopyDirectory(req.SourcePath, dest);
            }
            else
            {
                Directory.CreateDirectory(dest);
                // Scaffold a minimal prompt.md
                File.WriteAllText(Path.Combine(dest, "prompt.md"), $"# {req.Name}\n\n");
            }

            return Results.Created($"/api/skills/{req.Name}", new SkillCreatedResponse(req.Name, dest));
        })
        .WithName("InstallSkill");

        // DELETE /api/skills/{name} — remove skill
        group.MapDelete("/{name}", (string name) =>
        {
            if (!IsValidSkillName(name))
                return Results.BadRequest(new ErrorResponse("Invalid skill name."));

            var dir = Path.Combine(SkillsDir, name);
            if (!IsContainedInSkillsDir(dir))
                return Results.BadRequest(new ErrorResponse("Invalid skill name."));

            if (!Directory.Exists(dir))
                return Results.NotFound(new ErrorResponse($"Skill '{name}' not found."));

            Directory.Delete(dir, recursive: true);
            return Results.NoContent();
        })
        .WithName("DeleteSkill");

        return app;
    }

    /// <summary>
    /// Returns true when <paramref name="name"/> is a valid skill name:
    /// non-empty, not "." or "..", and contains no path separators.
    /// This is an early-out check before Path.Combine is called.
    /// </summary>
    internal static bool IsValidSkillName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (name is "." or "..")
            return false;

        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
            return false;

        return true;
    }

    /// <summary>
    /// Returns true when the resolved <paramref name="candidatePath"/> is strictly
    /// contained within <see cref="SkillsDir"/>, preventing directory traversal.
    /// </summary>
    internal static bool IsContainedInSkillsDir(string candidatePath)
    {
        var fullBase = Path.GetFullPath(SkillsDir) + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidatePath);
        return fullCandidate.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }
}

internal sealed record InstallSkillRequest(string Name, string? SourcePath);
#pragma warning restore IL2026
