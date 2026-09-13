using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Common;

namespace WeaveFleet.Application.SessionSources;

/// <summary>The optional base a new worktree starts from, as the repository and GitHub sources accept it.</summary>
public static class WorktreeBaseInput
{
    /// <summary>
    /// A trimmed base branch, or null when none was given. Fails for anything but a new worktree,
    /// and for a name git couldn't take as a branch.
    /// </summary>
    public static Result<string?> Normalize(string? baseBranch, string isolationStrategy, bool usesExistingWorktree)
    {
        if (string.IsNullOrWhiteSpace(baseBranch))
            return Result.Success<string?>(null);

        var value = baseBranch.Trim();
        if (!string.Equals(isolationStrategy, "worktree", StringComparison.Ordinal) || usesExistingWorktree)
        {
            return FleetError.ValidationError(
                "SessionSource.Input.BaseBranch",
                "A base branch can only be chosen for a new worktree.");
        }

        if (!WorkspaceService.IsValidBranchName(value))
        {
            return FleetError.ValidationError(
                "SessionSource.Input.BaseBranch",
                $"'{value}' is not a valid branch name.");
        }

        return value;
    }
}
