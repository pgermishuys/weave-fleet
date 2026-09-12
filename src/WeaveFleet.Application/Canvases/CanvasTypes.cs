using System.Diagnostics.CodeAnalysis;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Canvases;

public static class CanvasKinds
{
    /// <summary>Boxes and edges. The agent edits the content; the user moves and removes boxes.</summary>
    public const string Diagram = "diagram";

    /// <summary>Mermaid sequence diagram source. Only the agent edits it.</summary>
    public const string Sequence = "sequence";

    public static bool IsKnown(string kind) => kind is Diagram or Sequence;
}

public static class CanvasLimits
{
    public const int MaxStateBytes = 256 * 1024;
    public const int MaxDiagramNodes = 200;
}

public enum CanvasActor
{
    Agent,
    User,
}

public static class CanvasActorExtensions
{
    /// <summary>The value stored in <see cref="CanvasRevision.Actor"/>.</summary>
    public static string ToRevisionActor(this CanvasActor actor)
        => actor == CanvasActor.Agent ? CanvasRevision.AgentActor : CanvasRevision.UserActor;
}

public enum CanvasErrorKind
{
    /// <summary>The change is malformed or breaks a rule: an unknown op, an op the actor can't send, a bad value, a duplicate id.</summary>
    Invalid,

    /// <summary>An op names a box or edge that isn't on the canvas.</summary>
    UnknownId,

    /// <summary>An agent op touches something the user removed since the agent last read or wrote the canvas.</summary>
    Refused,

    /// <summary>The change would take the canvas over <see cref="CanvasLimits"/>.</summary>
    TooLarge,
}

/// <summary>A rejected change. <see cref="Message"/> is short text written for the agent (or the user) to read.</summary>
public sealed record CanvasError(CanvasErrorKind Kind, string Message);

public sealed class CanvasResult<T>
    where T : class
{
    internal CanvasResult(T? value, CanvasError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }
    public CanvasError? Error { get; }

    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;
}

public static class CanvasResult
{
    public static CanvasResult<T> Ok<T>(T value)
        where T : class
        => new(value, null);

    public static CanvasResult<T> Fail<T>(CanvasError error)
        where T : class
        => new(null, error);

    public static CanvasResult<T> Fail<T>(CanvasErrorKind kind, string message)
        where T : class
        => new(null, new CanvasError(kind, message));
}
