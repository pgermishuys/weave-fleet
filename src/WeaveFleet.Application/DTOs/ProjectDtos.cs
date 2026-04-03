namespace WeaveFleet.Application.DTOs;

/// <summary>Request DTO for creating a new project.</summary>
public sealed record CreateProjectRequest(string Name, string? Description = null);

/// <summary>Request DTO for updating a project's name/description.</summary>
public sealed record UpdateProjectRequest(string? Name, string? Description);

/// <summary>Request DTO for deleting a project.</summary>
public sealed record DeleteProjectRequest(string Mode); // "move_to_scratch" | "delete_sessions"

/// <summary>Request DTO for reordering a project.</summary>
public sealed record ReorderProjectRequest(int Position);

/// <summary>Response DTO for a project, including aggregated session count.</summary>
public sealed record ProjectResponse(
    string Id,
    string Name,
    string? Description,
    string Type,
    int Position,
    int SessionCount,
    string CreatedAt,
    string UpdatedAt);
