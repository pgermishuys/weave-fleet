using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Skills;

namespace WeaveFleet.Api.Endpoints;

/// <summary>
/// Reads the install scope of a skill or tool request: <c>"global"</c> (the default) or <c>"project"</c> plus a repository path.
/// </summary>
internal static class InstallTargets
{
    public const string Global = "global";
    public const string Project = "project";

    /// <summary>
    /// For a new install: a project must be a git repository inside the workspace roots.
    /// </summary>
    public static async Task<Result<InstallTarget>> ResolveAsync(
        string? scope,
        string? projectPath,
        RepositoryService repositories,
        CancellationToken ct)
    {
        var parsed = ParseScope(scope);
        if (parsed.IsFailure)
            return parsed.Error;

        if (parsed.Value == InstallScope.Global)
            return InstallTarget.Global;

        if (string.IsNullOrWhiteSpace(projectPath))
            return FleetError.ValidationError("ProjectPath", "Choose the repository to install into.");

        var resolved = await repositories.ResolveRepositoryPathAsync(projectPath, ct);
        if (resolved.IsFailure)
            return resolved.Error;

        return InstallTarget.Project(resolved.Value);
    }

    /// <summary>
    /// For an existing install: matched against the manifest as stored, so it still works after
    /// the repository has moved or been deleted.
    /// </summary>
    public static Result<InstallTarget> Parse(string? scope, string? projectPath)
    {
        var parsed = ParseScope(scope);
        if (parsed.IsFailure)
            return parsed.Error;

        if (parsed.Value == InstallScope.Project && string.IsNullOrWhiteSpace(projectPath))
            return FleetError.ValidationError("ProjectPath", "A project install needs its repository path.");

        return InstallTarget.From(parsed.Value, projectPath);
    }

    public static string ToApi(InstallScope scope) => scope == InstallScope.Project ? Project : Global;

    /// <summary>Maps an install failure to a response: 409 for a clash, 400 for bad input, 404, else 500.</summary>
    public static IResult ToErrorResult(FleetError error, string title) => error.Code switch
    {
        var c when c == FleetError.Conflict.Code => Results.Conflict(new ErrorResponse(error.Description)),
        var c when c.StartsWith("Validation.", StringComparison.Ordinal) => Results.BadRequest(new ErrorResponse(error.Description)),
        var c when c.EndsWith(".NotFound", StringComparison.Ordinal) => Results.NotFound(new ErrorResponse(error.Description)),
        _ => Results.Problem(statusCode: 500, title: title, detail: error.Description)
    };

    private static Result<InstallScope> ParseScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope.Equals(Global, StringComparison.OrdinalIgnoreCase))
            return InstallScope.Global;

        if (scope.Equals(Project, StringComparison.OrdinalIgnoreCase))
            return InstallScope.Project;

        return FleetError.ValidationError("Scope", "Scope must be 'global' or 'project'.");
    }
}
