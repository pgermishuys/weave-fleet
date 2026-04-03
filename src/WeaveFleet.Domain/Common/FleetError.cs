namespace WeaveFleet.Domain.Common;

/// <summary>
/// Represents a domain error with a code and human-readable description.
/// Named <c>FleetError</c> to avoid conflict with VB reserved keyword 'Error' (CA1716).
/// </summary>
public sealed record FleetError(string Code, string Description)
{
    public static readonly FleetError None = new(string.Empty, string.Empty);
    public static readonly FleetError NotFound = new("General.NotFound", "The requested resource was not found.");
    public static readonly FleetError Unauthorized = new("General.Unauthorized", "Authorization failed.");
    public static readonly FleetError Conflict = new("General.Conflict", "A conflict occurred.");
    public static readonly FleetError Unexpected = new("General.Unexpected", "An unexpected error occurred.");

    public static FleetError NotFoundFor(string resource, object id) =>
        new($"{resource}.NotFound", $"{resource} with id '{id}' was not found.");

    public static FleetError ValidationError(string field, string message) =>
        new($"Validation.{field}", message);

    public override string ToString() => $"{Code}: {Description}";
}
