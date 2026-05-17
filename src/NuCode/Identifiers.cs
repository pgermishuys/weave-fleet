using System.Diagnostics.CodeAnalysis;

namespace NuCode;

/// <summary>
/// Strongly-typed identifier for a session.
/// </summary>
public readonly record struct SessionId(string Value)
{
    public static SessionId New() => new(Ulid.NewUlid().ToString());

    public override string ToString() => Value;

    [return: NotNullIfNotNull(nameof(value))]
    public static implicit operator string?(SessionId? value) => value?.Value;
}

/// <summary>
/// Strongly-typed identifier for a message.
/// </summary>
public readonly record struct MessageId(string Value)
{
    public static MessageId New() => new(Ulid.NewUlid().ToString());

    public override string ToString() => Value;

    [return: NotNullIfNotNull(nameof(value))]
    public static implicit operator string?(MessageId? value) => value?.Value;
}

/// <summary>
/// Strongly-typed identifier for a message part.
/// </summary>
public readonly record struct PartId(string Value)
{
    public static PartId New() => new(Ulid.NewUlid().ToString());

    public override string ToString() => Value;

    [return: NotNullIfNotNull(nameof(value))]
    public static implicit operator string?(PartId? value) => value?.Value;
}

/// <summary>
/// Strongly-typed identifier for a tool.
/// </summary>
public readonly record struct ToolId(string Value)
{
    public static ToolId New() => new(Ulid.NewUlid().ToString());

    public override string ToString() => Value;

    [return: NotNullIfNotNull(nameof(value))]
    public static implicit operator string?(ToolId? value) => value?.Value;
}

/// <summary>
/// Strongly-typed identifier for an agent profile.
/// </summary>
public readonly record struct AgentId(string Value)
{
    public static AgentId New() => new(Ulid.NewUlid().ToString());

    public override string ToString() => Value;

    [return: NotNullIfNotNull(nameof(value))]
    public static implicit operator string?(AgentId? value) => value?.Value;
}
