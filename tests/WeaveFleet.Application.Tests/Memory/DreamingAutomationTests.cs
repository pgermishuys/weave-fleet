using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Automations;
using WeaveFleet.Application.Memory.Dreaming;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Memory;

public sealed class DreamingAutomationTests : IDisposable
{
    private const string Owner = "owner";
    private readonly string _root = Directory.CreateTempSubdirectory("fleet-night-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 8, 0, 0, TimeSpan.Zero));
    private readonly InMemoryUserPreferenceRepository _preferences = new();
    private readonly InMemoryAutomationRunRepository _runs = new();
    private readonly InMemorySessionRepository _sessions = new();
    private readonly InMemoryDreamStore _store = new();
    private readonly FakeStarter _starter;
    private readonly DreamingAutomation _sut;
    private readonly Automation _automation = new() { Id = "auto-1", Name = "Dreaming", Builtin = BuiltinAutomations.Dreaming, UserId = Owner, HarnessType = "opencode2", Model = "anthropic/sonnet" };

    public DreamingAutomationTests()
    {
        _preferences.Seed(DreamRules.PreferenceKey, "true");
        _starter = new FakeStarter(_store, _sessions, _time);
        var services = new ServiceCollection();
        services.AddSingleton<IBackgroundUserScope, NoUserScope>();
        services.AddSingleton(_ => _sut!);
        _sut = new DreamingAutomation(
            new DreamingFeature(_preferences), _preferences, _runs, _sessions, _store, _starter, new NoUserScope(),
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _time, NullLogger<DreamingAutomation>.Instance)
        { PollEvery = TimeSpan.Zero };
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private void Session(string id, string directory, TimeSpan ago, bool hidden = false, params string[] tags)
        => _sessions.Seed(new Session { Id = id, Directory = directory, CreatedAt = (_time.GetUtcNow() - ago).UtcDateTime.ToString("O"), IsHidden = hidden, Tags = [.. tags], UserId = Owner });

    private async Task<AutomationRun> BeginRunAsync(string id = "run-now")
    {
        var run = new AutomationRun { Id = id, AutomationId = _automation.Id, UserId = Owner, Trigger = "schedule", StartedAt = _time.GetUtcNow().UtcDateTime.ToString("O"), Status = "starting" };
        await _runs.InsertAsync(run);
        return run;
    }

    private async Task<AutomationRun> WaitSettledAsync(string id)
    {
        for (var i = 0; i < 300 && _runs.All.Single(r => r.Id == id).SettledState is null; i++)
            await Task.Delay(10);
        return _runs.All.Single(r => r.Id == id);
    }

    [Fact]
    public async Task A_night_dreams_each_repository_busiest_first_and_settles_with_a_summary()
    {
        var alpha = Folder("alpha");
        var beta = Folder("beta");
        Session("s1", beta, TimeSpan.FromHours(1));
        Session("s2", alpha, TimeSpan.FromHours(2));
        Session("s3", alpha, TimeSpan.FromHours(3));
        _starter.Plan[alpha] = new Night(Tokens: 2000, Kinds: ["added"]);
        _starter.Plan[beta] = new Night(Tokens: 1000, Kinds: []);
        var since = _time.GetUtcNow().AddDays(-1);
        var run = await BeginRunAsync();

        var repositories = await _sut.RepositoriesSinceAsync(since);
        await _sut.NightAsync(_automation, run.Id, since, repositories);

        _starter.Requests.Select(r => r.Repository).ShouldBe([alpha, beta]);
        _starter.Requests.ShouldAllBe(r => r.Since == since && r.RunId == run.Id && r.HarnessType == "opencode2" && r.ProviderId == "anthropic" && r.ModelId == "sonnet");
        _store.All.Select(d => d.Tokens).ShouldBe([2000, 1000], ignoreOrder: true);
        var settled = _runs.All.Single();
        settled.SettledState.ShouldBe("done");
        settled.Summary.ShouldBe("alpha: 1 note added · beta: no changes · 1 min, 3k tokens");
    }

    [Fact]
    public async Task A_night_stops_starting_dreams_once_its_budget_is_spent()
    {
        var alpha = Folder("alpha");
        var beta = Folder("beta");
        Session("s1", alpha, TimeSpan.FromHours(1));
        Session("s2", alpha, TimeSpan.FromHours(2));
        Session("s3", beta, TimeSpan.FromHours(3));
        _preferences.Seed(DreamRules.BudgetPreferenceKey, "10000");
        _starter.Plan[alpha] = new Night(Tokens: 12_000, Kinds: ["added"]);
        var run = await BeginRunAsync();
        var since = _time.GetUtcNow().AddDays(-1);

        await _sut.NightAsync(_automation, run.Id, since, await _sut.RepositoriesSinceAsync(since));

        _starter.Requests.ShouldHaveSingleItem().Repository.ShouldBe(alpha);
        var settled = _runs.All.Single();
        settled.Summary.ShouldBe("alpha: 1 note added · beta: not read, the night's budget was spent · 1 min, 12k tokens");
        settled.SettledState.ShouldBe("done");
    }

    [Fact]
    public async Task A_dream_that_failed_puts_its_reason_in_the_summary_and_the_night_fails_when_none_were_done()
    {
        var alpha = Folder("alpha");
        Session("s1", alpha, TimeSpan.FromHours(1));
        _starter.Plan[alpha] = new Night(Tokens: 500, Kinds: [], Status: "failed", Error: "The model wasn't found.");
        var run = await BeginRunAsync();
        var since = _time.GetUtcNow().AddDays(-1);

        await _sut.NightAsync(_automation, run.Id, since, await _sut.RepositoriesSinceAsync(since));

        var settled = _runs.All.Single();
        settled.Summary.ShouldBe("alpha: The model wasn't found · 1 min, 500 tokens");
        settled.SettledState.ShouldBe("failed");
    }

    [Fact]
    public async Task A_dream_that_cant_start_is_named_in_the_summary()
    {
        var alpha = Folder("alpha");
        Session("s1", alpha, TimeSpan.FromHours(1));
        _starter.Refusal = "OpenCode 2 isn't installed.";
        var run = await BeginRunAsync();
        var since = _time.GetUtcNow().AddDays(-1);

        await _sut.NightAsync(_automation, run.Id, since, await _sut.RepositoriesSinceAsync(since));

        _runs.All.Single().Summary.ShouldStartWith("alpha: OpenCode 2 isn't installed");
        _runs.All.Single().SettledState.ShouldBe("failed");
    }

    [Fact]
    public async Task A_run_with_new_sessions_writes_its_summary_and_the_night_settles_in_the_background()
    {
        var alpha = Folder("alpha");
        Session("s1", alpha, TimeSpan.FromHours(1));
        _starter.Plan[alpha] = new Night(Tokens: 1500, Kinds: ["added"]);
        var run = await BeginRunAsync();

        var outcome = await _sut.RunAsync(_automation, run);

        outcome.Summary.ShouldBe("Reading alpha…");
        outcome.Skipped.ShouldBeFalse();
        (await WaitSettledAsync(run.Id)).Summary.ShouldBe("alpha: 1 note added · 1 min, 2k tokens");
    }

    [Fact]
    public async Task A_run_without_new_sessions_is_skipped_and_writes_nothing()
    {
        Session("old", Folder("alpha"), TimeSpan.FromHours(30));
        Session("tagged", Folder("beta"), TimeSpan.FromHours(1), hidden: false, DreamRules.SessionTag);
        Session("hidden", Folder("gamma"), TimeSpan.FromHours(1), hidden: true);
        var run = await BeginRunAsync();

        var outcome = await _sut.RunAsync(_automation, run);

        outcome.Skipped.ShouldBeTrue();
        outcome.Error!.ShouldContain("no new sessions");
        _starter.Requests.ShouldBeEmpty();
        _runs.All.Single().Summary.ShouldBeNull();
    }

    [Fact]
    public async Task A_night_reads_back_to_the_start_of_the_last_night()
    {
        var alpha = Folder("alpha");
        Session("before-last-night", alpha, TimeSpan.FromHours(13));
        await _runs.InsertAsync(new AutomationRun
        {
            Id = "run-last", AutomationId = _automation.Id, UserId = Owner, Trigger = "schedule", Status = "started",
            StartedAt = (_time.GetUtcNow() - TimeSpan.FromHours(12)).UtcDateTime.ToString("O"), Summary = "alpha: no changes", SettledState = "done",
        });
        var run = await BeginRunAsync();

        var skipped = await _sut.RunAsync(_automation, run);
        Session("after-last-night", alpha, TimeSpan.FromHours(11));
        var read = await _sut.RunAsync(_automation, await BeginRunAsync("run-next"));

        skipped.Skipped.ShouldBeTrue();
        read.Summary.ShouldBe("Reading alpha…");
        (await WaitSettledAsync("run-next")).Summary!.ShouldContain("alpha");
        _starter.Requests.ShouldHaveSingleItem().Since.ShouldBe(_time.GetUtcNow() - TimeSpan.FromHours(12));
    }

    [Fact]
    public async Task A_run_is_skipped_while_the_last_night_is_still_dreaming()
    {
        Session("s1", Folder("alpha"), TimeSpan.FromHours(1));
        await _runs.InsertAsync(new AutomationRun
        {
            Id = "run-last", AutomationId = _automation.Id, UserId = Owner, Trigger = "schedule", Status = "started",
            StartedAt = (_time.GetUtcNow() - TimeSpan.FromHours(2)).UtcDateTime.ToString("O"), Summary = "Dreaming about alpha (1 of 1)…",
        });

        var outcome = await _sut.RunAsync(_automation, await BeginRunAsync());

        outcome.Skipped.ShouldBeTrue();
        outcome.Error!.ShouldContain("still dreaming");
        _starter.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_run_is_skipped_while_dreaming_is_off()
    {
        _preferences.Seed(DreamRules.PreferenceKey, "false");
        Session("s1", Folder("alpha"), TimeSpan.FromHours(1));

        (await _sut.IsOnAsync(_automation)).ShouldBeFalse();
        var outcome = await _sut.RunAsync(_automation, await BeginRunAsync());

        outcome.Skipped.ShouldBeTrue();
        outcome.Error!.ShouldContain("Dreaming is off");
        _starter.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(950L, "950")]
    [InlineData(12_345L, "12k")]
    [InlineData(1_500_000L, "1.5M")]
    [InlineData(2_000_000L, "2M")]
    public void Tokens_are_written_short(long tokens, string expected)
        => DreamingAutomation.FormatTokens(tokens).ShouldBe(expected);

    [Fact]
    public void A_dream_is_described_by_what_it_changed()
    {
        Dream Done(params string[] kinds) => new() { Status = "done", Changes = [.. kinds.Select(k => new DreamChange { Kind = k })] };

        DreamingAutomation.Describe("orchard", Done()).ShouldBe("orchard: no changes");
        DreamingAutomation.Describe("orchard", Done("left", "skipped")).ShouldBe("orchard: no changes");
        DreamingAutomation.Describe("orchard", Done("added")).ShouldBe("orchard: 1 note added");
        DreamingAutomation.Describe("orchard", Done("added", "merged", "left")).ShouldBe("orchard: 2 notes changed");
        DreamingAutomation.Describe("orchard", new Dream { Status = "failed", Error = "Ran out of time." }).ShouldBe("orchard: Ran out of time");
    }

    private sealed record Night(int Tokens, string[] Kinds, string Status = "done", string? Error = null);

    private sealed class FakeStarter(InMemoryDreamStore store, InMemorySessionRepository sessions, TimeProvider time) : IDreamStarter
    {
        public List<StartDreamRequest> Requests { get; } = [];
        public Dictionary<string, Night> Plan { get; } = [];
        public string? Refusal { get; set; }

        public async Task<Result<DreamView>> StartAsync(StartDreamRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Refusal is not null)
                return FleetError.ValidationError("Harness", Refusal);

            var plan = Plan.GetValueOrDefault(request.Repository!) ?? new Night(0, []);
            var sessionId = $"dreaming-{Requests.Count}";
            sessions.Seed(new Session { Id = sessionId, TotalTokens = plan.Tokens, Tags = [DreamRules.SessionTag], UserId = Owner });
            var dream = new Dream
            {
                Repository = request.Repository!, SessionId = sessionId, Status = plan.Status, Error = plan.Error, Started = time.GetUtcNow(),
                Finished = time.GetUtcNow(), RunId = request.RunId, Since = request.Since,
                Changes = [.. plan.Kinds.Select(k => new DreamChange { Kind = k })],
            };
            await store.SaveAsync(Owner, dream, ct);
            return Result.Success(DreamView.Of(dream));
        }
    }

    internal sealed class NoUserScope : IBackgroundUserScope
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
