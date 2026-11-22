using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Tests.Sessions;

public sealed class LostWorkNoteTests
{
    private static readonly string Outside = WorkLostCause.StoppedFromOutside("Fleet's OpenCode 2 server", 7, new DateTimeOffset(2026, 11, 22, 9, 5, 0, TimeSpan.Zero), null);

    private static Delegation Work(string label, string? detail = null) => new()
    {
        Id = label, ParentSessionId = "s", Title = "t", Label = label, Kind = WorkKinds.Shell, Detail = detail, EndedReason = WorkEndedReasons.Lost,
    };

    [Fact]
    public void Nothing_lost_is_no_note() => LostWorkNote.For([]).ShouldBeNull();

    [Fact]
    public void An_unknown_cause_keeps_the_generic_text()
    {
        var note = LostWorkNote.For([Work("a", "exit 1")])!;
        note.ShouldContain("because Fleet or the agent's process restarted");
        note.ShouldNotContain("FLEET_HARNESS_SERVER_PIDS");
    }

    [Fact]
    public void A_server_stopped_from_outside_is_named_without_the_exit_code_when_unknown()
    {
        var note = LostWorkNote.For([Work("a", Outside)])!;
        note.ShouldStartWith("Note from Fleet: Fleet's OpenCode 2 server (pid 7) was stopped from outside Fleet at 09:05 UTC, and this background work stopped with it.");
        note.ShouldNotContain("exit code");
        note.ShouldNotContain("restarted");
    }

    [Fact]
    public void Entries_are_capped_across_causes()
    {
        var work = Enumerable.Range(0, 8).Select(i => Work($"o{i}", Outside)).Concat(Enumerable.Range(0, 5).Select(i => Work($"g{i}"))).ToList();
        var note = LostWorkNote.For(work)!;
        note.Split('\n').Count(line => line.StartsWith("- shell:", StringComparison.Ordinal)).ShouldBe(LostWorkNote.MaxEntries);
        note.ShouldEndWith("- and 3 more");
    }
}
