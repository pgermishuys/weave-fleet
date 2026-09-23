using System.Diagnostics;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Tests.Harnesses;

/// <summary>How Fleet tells one process from a later one that got the same pid.</summary>
public sealed class ProcessIdentityTests
{
    private const string BootId = "7c3a0f6e-1d2b-4c5d-8e9f-0a1b2c3d4e5f";

    [Fact]
    public void A_stat_line_gives_the_pid_the_start_time_and_the_name()
    {
        var identity = ProcessIdentity.ParseLinuxStat(Stat(4242, "opencode", "S", 987654), BootId);

        identity.ShouldBe(new ProcessIdentity(4242, 987654, BootId, "opencode"));
    }

    [Fact]
    public void A_name_with_spaces_and_parentheses_doesnt_shift_the_fields()
    {
        var identity = ProcessIdentity.ParseLinuxStat(Stat(17, "a) b (c", "R", 12345), BootId).ShouldNotBeNull();

        identity.Name.ShouldBe("a) b (c");
        identity.StartTime.ShouldBe(12345);
    }

    [Fact]
    public void A_zombie_counts_as_gone()
        => ProcessIdentity.ParseLinuxStat(Stat(17, "opencode", "Z", 12345), BootId).ShouldBeNull();

    [Theory]
    [InlineData("")]
    [InlineData("17 opencode S 1")]
    [InlineData("x (opencode) S 1 1 1")]
    [InlineData("17 (opencode) S 1 1 1")]
    public void A_line_that_isnt_a_stat_line_gives_nothing(string stat)
        => ProcessIdentity.ParseLinuxStat(stat, BootId).ShouldBeNull();

    [Fact]
    public void Another_start_time_or_boot_is_another_process_and_the_name_doesnt_matter()
    {
        var started = new ProcessIdentity(4242, 987654, BootId, "opencode");

        started.IsSameProcess(started with { Name = "renamed" }).ShouldBeTrue();
        started.IsSameProcess(started with { StartTime = 987655 }).ShouldBeFalse();
        started.IsSameProcess(started with { BootId = "another-boot" }).ShouldBeFalse();
        started.IsSameProcess(started with { Pid = 4243 }).ShouldBeFalse();
        started.IsSameProcess(null).ShouldBeFalse();
    }

    [Fact]
    public async Task A_running_process_reads_the_same_each_time_and_not_at_all_once_it_has_exited()
    {
        if (!ProcessIdentity.IsSupported)
            return;

        using var sleep = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;
        try
        {
            var first = ProcessIdentity.Read(sleep.Id).ShouldNotBeNull();
            ProcessIdentity.Read(sleep.Id).ShouldBe(first);
            first.Name.ShouldBe("sleep");
            first.IsSameProcess(ProcessIdentity.Read(Environment.ProcessId)).ShouldBeFalse();
        }
        finally
        {
            sleep.Kill();
            await sleep.WaitForExitAsync();
        }

        ProcessIdentity.Read(sleep.Id).ShouldBeNull();
    }

    private static string Stat(int pid, string name, string state, long startTime)
    {
        // Fields 3 to 21 (state … itrealvalue), then 22 (starttime) and a few after it, as the kernel writes them.
        var fields = new List<string> { state };
        fields.AddRange(Enumerable.Range(4, 18).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        fields.Add(startTime.ToString(System.Globalization.CultureInfo.InvariantCulture));
        fields.AddRange(["123456", "789", "18446744073709551615"]);
        return $"{pid} ({name}) {string.Join(' ', fields)}\n";
    }
}
