using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Tests.Harnesses;

/// <summary>
/// The records that let a Fleet stop the harness processes a Fleet that died left running, and nothing else: a pid
/// alone never gets a process killed.
/// </summary>
public sealed class HarnessProcessRecordsTests : IDisposable
{
    private const string Boot = "boot-1";

    private static readonly ProcessIdentity DeadFleet = new(100, 5000, Boot, "WeaveFleet.Api");
    private static readonly ProcessIdentity Server = new(200, 6000, Boot, "opencode");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"fleet-process-records-{Guid.NewGuid():N}");
    private readonly Dictionary<int, ProcessIdentity> _running = [];
    private readonly List<int> _killed = [];

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void A_process_left_by_a_Fleet_that_is_gone_is_stopped_and_its_record_removed()
    {
        Record(DeadFleet, Server);
        _running[Server.Pid] = Server;

        Records().StopLeftovers().ShouldBe(1);

        _killed.ShouldBe([Server.Pid]);
        Directory.GetFiles(_directory).ShouldBeEmpty();
    }

    [Fact]
    public void A_pid_that_now_belongs_to_another_process_is_left_alone()
    {
        Record(DeadFleet, Server);
        _running[Server.Pid] = Server with { StartTime = 7000, Name = "node" };

        Records().StopLeftovers().ShouldBe(0);

        _killed.ShouldBeEmpty();
        Directory.GetFiles(_directory).ShouldBeEmpty();
    }

    [Fact]
    public void A_pid_from_before_a_reboot_is_left_alone()
    {
        Record(DeadFleet, Server);
        _running[Server.Pid] = Server with { BootId = "boot-2" };

        Records().StopLeftovers().ShouldBe(0);

        _killed.ShouldBeEmpty();
    }

    [Fact]
    public void A_process_whose_Fleet_is_still_running_is_that_Fleets_to_stop()
    {
        var otherFleet = new ProcessIdentity(300, 5500, Boot, "WeaveFleet.Api");
        Record(otherFleet, Server);
        _running[otherFleet.Pid] = otherFleet;
        _running[Server.Pid] = Server;

        Records().StopLeftovers().ShouldBe(0);

        _killed.ShouldBeEmpty();
        Directory.GetFiles(_directory).ShouldHaveSingleItem();
    }

    [Fact]
    public void A_Fleet_whose_pid_went_to_another_process_is_gone()
    {
        Record(DeadFleet, Server);
        _running[DeadFleet.Pid] = DeadFleet with { StartTime = 9000 };
        _running[Server.Pid] = Server;

        Records().StopLeftovers().ShouldBe(1);

        _killed.ShouldBe([Server.Pid]);
    }

    [Fact]
    public void The_record_of_a_process_that_has_exited_is_removed()
    {
        Record(DeadFleet, Server);

        Records().StopLeftovers().ShouldBe(0);

        _killed.ShouldBeEmpty();
        Directory.GetFiles(_directory).ShouldBeEmpty();
    }

    [Fact]
    public void An_unreadable_record_is_removed_and_kills_nothing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "100-5000-200-6000.json"), "{ not json");
        File.WriteAllText(Path.Combine(_directory, "notes.txt"), "not a record");
        _running[Server.Pid] = Server;

        Records().StopLeftovers().ShouldBe(0);

        _killed.ShouldBeEmpty();
        Directory.GetFiles(_directory).Select(Path.GetFileName).ShouldBe(["notes.txt"]);
    }

    [Fact]
    public void A_record_goes_when_its_process_is_forgotten()
    {
        var records = Records(owner: DeadFleet);
        records.Add(Server, "/bin/opencode");
        records.Add(Server with { Pid = 201 }, "/bin/opencode");

        records.Remove(Server);

        var left = Directory.GetFiles(_directory).ShouldHaveSingleItem();
        File.ReadAllText(left).ShouldContain("\"pid\":201");
    }

    [Fact]
    public void Nothing_is_left_behind_when_there_is_no_folder_yet()
        => Records().StopLeftovers().ShouldBe(0);

    [Fact]
    public async Task Real_processes_only_the_one_a_dead_Fleet_recorded_is_stopped()
    {
        if (!ProcessIdentity.IsSupported)
            return;

        // A Fleet that has exited, a server it left running, a process that got a recorded server's pid (its record
        // says another start time), and one no Fleet recorded.
        using var fleet = Sleep();
        var deadFleet = ProcessIdentity.Read(fleet.Id).ShouldNotBeNull();
        fleet.Kill();
        await fleet.WaitForExitAsync();

        using var leftover = Sleep();
        using var reused = Sleep();
        using var unrecorded = Sleep();
        try
        {
            var records = new HarnessProcessRecords(_directory, NullLogger.Instance) { Owner = deadFleet };
            records.Add(ProcessIdentity.Read(leftover.Id).ShouldNotBeNull(), "sleep");
            records.Add(ProcessIdentity.Read(reused.Id).ShouldNotBeNull() with { StartTime = 1 }, "sleep");

            new HarnessProcessRecords(_directory, NullLogger.Instance).StopLeftovers().ShouldBe(1);

            await leftover.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            reused.HasExited.ShouldBeFalse();
            unrecorded.HasExited.ShouldBeFalse();
            Directory.GetFiles(_directory).ShouldBeEmpty();
        }
        finally
        {
            foreach (var process in new[] { leftover, reused, unrecorded })
            {
                if (!process.HasExited)
                    process.Kill();
            }
        }
    }

    private HarnessProcessRecords Records(ProcessIdentity? owner = null) => new(_directory, NullLogger.Instance)
    {
        Owner = owner ?? new ProcessIdentity(999, 1, Boot, "WeaveFleet.Api"),
        ReadIdentity = pid => _running.GetValueOrDefault(pid),
        KillTree = _killed.Add,
    };

    private void Record(ProcessIdentity fleet, ProcessIdentity process)
        => Records(owner: fleet).Add(process, "/home/user/.opencode/bin/opencode");

    private static Process Sleep()
        => Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false })!;
}
