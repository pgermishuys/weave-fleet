namespace WeaveFleet.Api.Endpoints;

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
                return Results.Ok(Array.Empty<object>());

            var skills = Directory.EnumerateDirectories(SkillsDir)
                .Select(dir => new
                {
                    name = Path.GetFileName(dir),
                    path = dir,
                    hasPrompt = File.Exists(Path.Combine(dir, "prompt.md"))
                               || File.Exists(Path.Combine(dir, "PROMPT.md"))
                })
                .ToArray();

            return Results.Ok(skills);
        })
        .WithName("ListSkills");

        // GET /api/skills/{name} — single skill detail
        group.MapGet("/{name}", (string name) =>
        {
            var dir = Path.Combine(SkillsDir, name);
            if (!Directory.Exists(dir))
                return Results.NotFound(new { error = $"Skill '{name}' not found." });

            var promptFile = Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            var prompt = promptFile is not null ? File.ReadAllText(promptFile) : null;

            return Results.Ok(new { name, path = dir, prompt });
        })
        .WithName("GetSkill");

        // POST /api/skills — install skill from a source path
        group.MapPost("", (InstallSkillRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Skill name is required." });

            var dest = Path.Combine(SkillsDir, req.Name);
            if (Directory.Exists(dest))
                return Results.Conflict(new { error = $"Skill '{req.Name}' already exists." });

            if (!string.IsNullOrEmpty(req.SourcePath))
            {
                if (!Directory.Exists(req.SourcePath))
                    return Results.BadRequest(new { error = "Source path does not exist." });

                CopyDirectory(req.SourcePath, dest);
            }
            else
            {
                Directory.CreateDirectory(dest);
                // Scaffold a minimal prompt.md
                File.WriteAllText(Path.Combine(dest, "prompt.md"), $"# {req.Name}\n\n");
            }

            return Results.Created($"/api/skills/{req.Name}", new { name = req.Name, path = dest });
        })
        .WithName("InstallSkill");

        // DELETE /api/skills/{name} — remove skill
        group.MapDelete("/{name}", (string name) =>
        {
            var dir = Path.Combine(SkillsDir, name);
            if (!Directory.Exists(dir))
                return Results.NotFound(new { error = $"Skill '{name}' not found." });

            Directory.Delete(dir, recursive: true);
            return Results.NoContent();
        })
        .WithName("DeleteSkill");

        return app;
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
