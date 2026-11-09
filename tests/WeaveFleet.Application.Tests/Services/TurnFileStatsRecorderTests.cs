using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

public sealed class TurnFileStatsRecorderTests
{
    private readonly FakeSource _source = new();
    private readonly FakeRepository _kept = new();
    private readonly TurnFileStatsRecorder _sut;

    public TurnFileStatsRecorderTests()
    {
        _sut = new TurnFileStatsRecorder(
            TestServiceScopeFactory.Create(services =>
            {
                services.AddSingleton<IBackgroundUserScope>(new NoUserScope());
                services.AddSingleton<ITurnFileStatsSource>(_source);
                services.AddSingleton<ITurnFileStatsRepository>(_kept);
            }),
            TimeProvider.System,
            NullLogger<TurnFileStatsRecorder>.Instance);
    }

    private static SessionIdled Idled() => new() { Payload = new SessionIdledPayload { SessionId = "session-1" } };

    [Fact]
    public async Task keeps_the_changed_files_as_a_turn_ends()
    {
        _source.Files = [new TurnFileStat("README.md", 3, 1)];

        _sut.Observe("session-1", "user-1", Idled());
        await _sut.Pending;

        var (sessionId, files) = _kept.Added.ShouldHaveSingleItem();
        sessionId.ShouldBe("session-1");
        files.ShouldBe([new TurnFileStat("README.md", 3, 1)]);
    }

    [Fact]
    public async Task keeps_nothing_when_the_folder_has_no_diff_or_for_other_events_or_without_an_owner()
    {
        _source.Files = null;
        _sut.Observe("session-1", "user-1", Idled());
        await _sut.Pending;
        _source.Files = [new TurnFileStat("a", 1, 1)];
        _sut.Observe("session-1", "user-1", new TurnFailed { Payload = new TurnFailedPayload { SessionId = "session-1", Error = new TurnError { Name = "x", Message = "y" } } });
        _sut.Observe("session-1", null, Idled());
        await _sut.Pending;

        _kept.Added.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_failing_read_does_not_throw_into_the_relay()
    {
        _source.Throw = true;
        _sut.Observe("session-1", "user-1", Idled());
        await _sut.Pending;

        _kept.Added.ShouldBeEmpty();
    }

    private sealed class FakeSource : ITurnFileStatsSource
    {
        public IReadOnlyList<TurnFileStat>? Files { get; set; }
        public bool Throw { get; set; }

        public Task<IReadOnlyList<TurnFileStat>?> ReadAsync(string sessionId, CancellationToken ct) =>
            Throw ? throw new InvalidOperationException("git is gone") : Task.FromResult(Files);
    }

    private sealed class FakeRepository : ITurnFileStatsRepository
    {
        public List<(string SessionId, IReadOnlyList<TurnFileStat> Files)> Added { get; } = [];

        public Task AddAsync(string sessionId, DateTimeOffset endedAt, IReadOnlyList<TurnFileStat> files)
        {
            Added.Add((sessionId, files));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TurnFileStats>> ListAsync(string sessionId) => Task.FromResult<IReadOnlyList<TurnFileStats>>([]);
    }

    private sealed class NoUserScope : IBackgroundUserScope
    {
        public IDisposable Begin(string userId) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
