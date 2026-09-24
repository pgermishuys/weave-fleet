using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Testing.Fakes;

/// <summary>Answers questions off the record from a script, in order, and remembers what it was asked and whether it was disposed.</summary>
public sealed class FakeOffTheRecordConversation : IOffTheRecordConversation
{
    private readonly Queue<Func<string, CancellationToken, Task<OffTheRecordAnswer?>>> _answers = new();

    public List<string> Prompts { get; } = [];

    public int Disposals { get; private set; }

    public FakeOffTheRecordConversation Answer(string? text, OffTheRecordTokens? tokens = null)
    {
        _answers.Enqueue((_, _) => Task.FromResult(text is null ? null : new OffTheRecordAnswer(text, tokens)));
        return this;
    }

    public FakeOffTheRecordConversation Answer(Func<string, CancellationToken, Task<OffTheRecordAnswer?>> answer)
    {
        _answers.Enqueue(answer);
        return this;
    }

    public Task<OffTheRecordAnswer?> AskAsync(string prompt, CancellationToken ct)
    {
        Prompts.Add(prompt);
        return _answers.TryDequeue(out var answer) ? answer(prompt, ct) : throw new InvalidOperationException($"No answer scripted for question {Prompts.Count}.");
    }

    public ValueTask DisposeAsync()
    {
        Disposals++;
        return ValueTask.CompletedTask;
    }
}
