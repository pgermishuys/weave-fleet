namespace WeaveFleet.Domain.Common;

/// <summary>
/// Represents a void/unit return type for operations that succeed with no value.
/// Use <see cref="Result{T}"/> with <c>Unit</c> for operations that can fail but return nothing on success.
/// </summary>
public readonly struct Unit : IEquatable<Unit>
{
    public static Unit Value => default;

    public bool Equals(Unit other) => true;
    public override bool Equals(object? obj) => obj is Unit;
    public override int GetHashCode() => 0;
    public override string ToString() => "()";

    public static bool operator ==(Unit left, Unit right) => true;
    public static bool operator !=(Unit left, Unit right) => false;
}
