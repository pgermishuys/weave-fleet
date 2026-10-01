using WeaveFleet.Application.Memory;

namespace WeaveFleet.Testing.Fakes;

/// <summary>Memory notes and the files sessions read, kept in memory.</summary>
public sealed class FakeMemoryStore : IMemoryStore
{
    public List<MemoryNote> Notes { get; } = [];
    public Dictionary<string, string> Context { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ContextRepository { get; } = new(StringComparer.Ordinal);
    public List<string> Writes { get; } = [];
    public string? Machine { get; private set; }
    public int MachineWrites { get; private set; }

    public Task<IReadOnlyList<MemoryNote>> ListAsync(string userId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MemoryNote>>([.. Notes]);

    public Task<IReadOnlyList<MemoryNote>> ListForAsync(string userId, string? repository, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MemoryNote>>([.. Notes.Where(note => note.List == MemoryList.Machine || (repository is not null && note.Repository == repository))]);

    public Task<MemoryNote?> FindAsync(string userId, string id, CancellationToken ct = default)
        => Task.FromResult(Notes.FirstOrDefault(note => note.Id == id));

    public Task SaveAsync(string userId, MemoryNote note, CancellationToken ct = default)
    {
        var index = Notes.FindIndex(existing => existing.Id == note.Id);
        if (index >= 0)
            Notes[index] = note;
        else
            Notes.Add(note);
        return Task.CompletedTask;
    }

    public Task<int> DeleteAsync(string userId, IReadOnlyCollection<string> ids, CancellationToken ct = default)
        => Task.FromResult(Notes.RemoveAll(note => ids.Contains(note.Id)));

    /// <summary>The days with a prompt: the machine's under the empty key, each repository's under its path.</summary>
    public Dictionary<string, SortedSet<DateOnly>> DaysUsed { get; } = new(StringComparer.Ordinal);

    public Task RecordDayUsedAsync(string userId, string? repository, DateOnly day, CancellationToken ct = default)
    {
        foreach (var key in repository is null ? [string.Empty] : new[] { string.Empty, repository })
        {
            if (!DaysUsed.TryGetValue(key, out var days))
                DaysUsed[key] = days = [];
            days.Add(day);
        }

        return Task.CompletedTask;
    }

    public Task<MemoryDaysUsed> ListDaysUsedAsync(string userId, string? repository, CancellationToken ct = default)
        => Task.FromResult(new MemoryDaysUsed(
            [.. DaysUsed.GetValueOrDefault(string.Empty) ?? []],
            repository is null ? [] : [.. DaysUsed.GetValueOrDefault(repository) ?? []]));

    public string ContextFolder(string userId) => "/memory/context";

    public Task WriteContextAsync(string userId, string directory, string repository, string content, CancellationToken ct = default)
    {
        Context[directory] = content;
        ContextRepository[directory] = repository;
        Writes.Add(directory);
        return Task.CompletedTask;
    }

    public Task WriteMachineContextAsync(string userId, string content, CancellationToken ct = default)
    {
        Machine = content;
        MachineWrites++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MemoryContextFolder>> ListContextFoldersAsync(string userId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MemoryContextFolder>>([.. Context.Keys.Select(directory => new MemoryContextFolder(directory, ContextRepository[directory]))]);

    public Task ForgetContextAsync(string userId, string directory, CancellationToken ct = default)
    {
        Context.Remove(directory);
        ContextRepository.Remove(directory);
        return Task.CompletedTask;
    }

    public Task ClearContextAsync(string userId, CancellationToken ct = default)
    {
        Machine = null;
        Context.Clear();
        ContextRepository.Clear();
        return Task.CompletedTask;
    }
}
