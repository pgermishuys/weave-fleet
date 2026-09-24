namespace WeaveFleet.Domain.Harnesses;

/// <summary>
/// Questions asked off the record: they leave nothing in any session's history, and no tools run. Each question sees
/// the ones before it and their answers. Disposing it deletes whatever the harness made for it, such as a fork.
/// </summary>
public interface IOffTheRecordConversation : IAsyncDisposable
{
    /// <summary>The answer's text, or null when the model gave none.</summary>
    Task<OffTheRecordAnswer?> AskAsync(string prompt, CancellationToken ct);
}

/// <summary>An answer, and what it cost when the harness says.</summary>
/// <param name="Tokens">Null when the harness doesn't report tokens for it.</param>
public sealed record OffTheRecordAnswer(string Text, OffTheRecordTokens? Tokens);

/// <summary>The tokens a question used: all of them, and how many of those were read from the provider's cache.</summary>
public sealed record OffTheRecordTokens(long Total, long FromCache)
{
    public static OffTheRecordTokens? Add(OffTheRecordTokens? first, OffTheRecordTokens? second)
        => first is null ? second : second is null ? first : new(first.Total + second.Total, first.FromCache + second.FromCache);
}
