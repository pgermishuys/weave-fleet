using WeaveFleet.Application.Jobs;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Tests.Jobs;

public sealed class JobMatchingTests
{
    private const string LedgerApi = "git@example.com:acme/ledger-api.git";

    private static readonly JobRepository _ledgerApi = new("/work/ledger-api", "ledger-api", "https://example.com/acme/ledger-api");
    private static readonly JobRepository _docsSite = new("/work/docs-site", "docs-site", "");

    private static JobMachine Machine(string name, int seats = 1, string[]? labels = null, bool local = false, bool answering = true, params JobRepository[] repositories) =>
        new(name, name, local, answering, HasHarness: true, seats, labels ?? [], repositories.Length == 0 ? [_ledgerApi] : repositories);

    private static Job Job(string repository = LedgerApi, string[]? labels = null, string? pinned = null, string state = JobStates.Waiting, string? machine = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        UserId = "u",
        Title = "t",
        Ask = "a",
        Repository = repository,
        Labels = labels ?? [],
        PinnedMachineId = pinned,
        State = state,
        MachineId = machine,
    };

    private static Dictionary<string, int> Used(params (string Machine, int Count)[] used) => used.ToDictionary(x => x.Machine, x => x.Count);

    [Fact]
    public void A_remote_matches_across_ssh_and_https_forms_and_case()
    {
        JobMatching.NormalizeRemote("git@example.com:acme/ledger-api.git").ShouldBe(JobMatching.NormalizeRemote("https://Example.com/acme/Ledger-API/"));
    }

    [Fact]
    public void A_job_without_a_remote_matches_the_repository_with_the_same_folder_name()
    {
        var job = Job(repository: "docs-site");

        JobMatching.RepositoryOn(job, Machine("atlas", repositories: _docsSite))!.Path.ShouldBe("/work/docs-site");
        JobMatching.RepositoryOn(job, Machine("mini"))!.ShouldBeNull();
    }

    [Fact]
    public void The_machine_that_is_this_one_wins_a_tie()
    {
        var machines = new[] { Machine("mini", seats: 4), Machine("atlas", seats: 2, local: true) };

        JobMatching.Pick(Job(), machines, Used())!.Value.Machine.Name.ShouldBe("atlas");
    }

    [Fact]
    public void A_full_machine_is_skipped_and_among_other_machines_the_one_with_more_seats_free_takes_it()
    {
        var machines = new[] { Machine("atlas", seats: 2, local: true), Machine("mini", seats: 3), Machine("build-box", seats: 1) };

        JobMatching.Pick(Job(), machines, Used(("atlas", 2)))!.Value.Machine.Name.ShouldBe("mini");
    }

    [Fact]
    public void A_job_needs_every_label_and_goes_where_they_are()
    {
        var machines = new[] { Machine("atlas", seats: 2, local: true), Machine("mini", labels: ["mac", "xcode"]) };

        JobMatching.Pick(Job(labels: ["MAC"]), machines, Used())!.Value.Machine.Name.ShouldBe("mini");
        JobMatching.Pick(Job(labels: ["mac", "gpu"]), machines, Used()).ShouldBeNull();
    }

    [Fact]
    public void A_pinned_job_goes_only_to_its_machine()
    {
        var machines = new[] { Machine("atlas", seats: 2, local: true), Machine("mini") };

        JobMatching.Pick(Job(pinned: "mini"), machines, Used())!.Value.Machine.Name.ShouldBe("mini");
        JobMatching.Pick(Job(pinned: "mini"), machines, Used(("mini", 1))).ShouldBeNull();
    }

    [Fact]
    public void A_machine_with_no_seats_or_that_does_not_answer_takes_nothing()
    {
        JobMatching.Pick(Job(), [Machine("atlas", seats: 0)], Used()).ShouldBeNull();
        JobMatching.Pick(Job(), [Machine("atlas", answering: false)], Used()).ShouldBeNull();
        JobMatching.Pick(Job(), [Machine("atlas") with { HasHarness = false }], Used()).ShouldBeNull();
    }

    [Fact]
    public void Seats_are_held_only_by_jobs_that_run_or_need_the_user()
    {
        var used = JobMatching.SeatsUsed([
            Job(state: JobStates.Running, machine: "atlas"),
            Job(state: JobStates.NeedsYou, machine: "atlas"),
            Job(state: JobStates.Done, machine: "atlas"),
            Job(state: JobStates.Waiting),
        ]);

        used["atlas"].ShouldBe(2);
        used.Count.ShouldBe(1);
    }

    [Fact]
    public void Waiting_says_when_no_seat_is_free()
    {
        JobMatching.WhyWaiting(Job(), [Machine("atlas", seats: 2, local: true)], Used(("atlas", 2))).ShouldBe("No machine has a free seat.");
    }

    [Fact]
    public void Waiting_names_the_only_machine_with_the_label_when_it_is_full()
    {
        var machines = new[] { Machine("atlas", seats: 2, local: true), Machine("mini", labels: ["mac"]) };

        JobMatching.WhyWaiting(Job(labels: ["mac"]), machines, Used(("mini", 1))).ShouldBe("Needs `mac`: only mini has it, and it's full.");
    }

    [Fact]
    public void Waiting_says_when_no_machine_has_the_repository_or_the_label()
    {
        var machines = new[] { Machine("atlas", seats: 2, local: true) };

        JobMatching.WhyWaiting(Job(repository: "https://example.com/acme/ledger-ios"), machines, Used()).ShouldBe("No machine has ledger-ios.");
        JobMatching.WhyWaiting(Job(labels: ["mac"]), machines, Used()).ShouldBe("No machine has `mac`.");
    }

    [Fact]
    public void Waiting_says_when_the_pinned_machine_does_not_answer()
    {
        var machines = new[] { Machine("atlas", seats: 2, local: true), Machine("mini", answering: false) };

        JobMatching.WhyWaiting(Job(pinned: "mini"), machines, Used()).ShouldBe("Pinned to mini, which isn't answering.");
    }

    [Fact]
    public void Waiting_says_when_no_machine_takes_jobs_yet()
    {
        JobMatching.WhyWaiting(Job(), [Machine("atlas", seats: 0)], Used()).ShouldBe("No machine takes jobs yet. Give one a seat in Settings.");
    }
}
