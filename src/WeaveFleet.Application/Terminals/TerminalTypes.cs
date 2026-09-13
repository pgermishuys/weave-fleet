using System.Diagnostics.CodeAnalysis;

namespace WeaveFleet.Application.Terminals;

public enum TerminalStatus
{
    /// <summary>A shell is running.</summary>
    Running,

    /// <summary>Saved from before Fleet restarted. Attaching starts a new shell under the old scrollback.</summary>
    Stopped,
}

/// <summary>A terminal tab in a session's drawer.</summary>
public sealed record TerminalInfo(string Id, string Title, TerminalStatus Status, DateTimeOffset CreatedAt);

/// <summary>Who owns the session a terminal belongs to, and where its shell starts.</summary>
public sealed record TerminalContext(string SessionId, string UserId, string Directory);

public enum TerminalErrorKind
{
    /// <summary>The session or terminal doesn't exist, or belongs to someone else. Also used when terminals are off.</summary>
    NotFound,

    /// <summary>The session can't have terminals right now, e.g. it's archived or its folder is gone.</summary>
    Unavailable,

    /// <summary>A limit on terminals per session or across Fleet was reached.</summary>
    LimitReached,

    /// <summary>No shell could be started.</summary>
    SpawnFailed,
}

/// <summary>A refused terminal request. <see cref="Message"/> is written for the user to read.</summary>
public sealed record TerminalError(TerminalErrorKind Kind, string Message);

public sealed class TerminalResult<T>
    where T : class
{
    internal TerminalResult(T? value, TerminalError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }
    public TerminalError? Error { get; }

    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;
}

public static class TerminalResult
{
    public static TerminalResult<T> Ok<T>(T value)
        where T : class
        => new(value, null);

    public static TerminalResult<T> Fail<T>(TerminalErrorKind kind, string message)
        where T : class
        => new(null, new TerminalError(kind, message));

    public static TerminalResult<T> Fail<T>(TerminalError error)
        where T : class
        => new(null, error);
}

public enum TerminalFrameKind
{
    /// <summary>Output bytes from the shell.</summary>
    Output,

    /// <summary>Someone cleared the terminal.</summary>
    Cleared,

    /// <summary>The shell ended. <see cref="TerminalFrame.ExitCode"/> is null when Fleet ended it.</summary>
    Exited,
}

/// <summary>One thing an attached client receives, in order.</summary>
public sealed record TerminalFrame(TerminalFrameKind Kind, byte[]? Data = null, int? ExitCode = null)
{
    public static readonly TerminalFrame Cleared = new(TerminalFrameKind.Cleared);

    public static TerminalFrame Output(byte[] data) => new(TerminalFrameKind.Output, data);

    public static TerminalFrame Exited(int? exitCode) => new(TerminalFrameKind.Exited, ExitCode: exitCode);
}

/// <summary>Thrown into a client's frames when it falls too far behind the shell's output.</summary>
public sealed class TerminalClientTooSlowException : Exception
{
    public TerminalClientTooSlowException()
        : base("The terminal client fell too far behind and was disconnected.")
    {
    }
}
