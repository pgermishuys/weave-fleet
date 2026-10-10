using Shouldly;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;

namespace WeaveFleet.Application.Tests.Mods.Host;

public sealed class ModStrikesTests
{
    private const string Mod = "test-chips@v1";

    [Fact]
    public void A_failure_adds_one_or_takes_the_hosts_count_when_higher()
    {
        var strikes = new ModStrikes();

        strikes.Failed(Mod, 1).ShouldBe(1);
        strikes.Failed(Mod, 1).ShouldBe(2);
        strikes.Of(Mod).ShouldBe(2);
        strikes.Of("demo-mod@v1").ShouldBe(0);

        var fresh = new ModStrikes();
        fresh.Failed(Mod, 2).ShouldBe(2);
    }

    [Fact]
    public void A_hang_adds_one_and_a_reset_starts_over()
    {
        var strikes = new ModStrikes();
        strikes.Failed(Mod, 1);

        strikes.Hung(Mod).ShouldBe(2);
        strikes.Reset(Mod);

        strikes.Of(Mod).ShouldBe(0);
        strikes.Hung(Mod).ShouldBe(1);
    }

    [Fact]
    public void Counts_are_safe_across_threads()
    {
        var strikes = new ModStrikes();

        Parallel.For(0, 1000, _ => strikes.Hung(Mod));

        strikes.Of(Mod).ShouldBe(1000);
    }
}

public sealed class ModLogBookTests
{
    private static ModLogLine Line(int n, string? sessionId = null)
        => new(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero).AddSeconds(n), "info", $"line {n}", sessionId);

    [Fact]
    public void Keeps_each_mods_lines_oldest_first_per_user()
    {
        var book = new ModLogBook();

        book.Add("test-user", "test-chips@v1", Line(1));
        book.Add("test-user", "test-chips@v1", Line(2, "ses_test1"));
        book.Add("test-user", "demo-mod@v1", Line(3));
        book.Add("other-user", "test-chips@v1", Line(4));

        book.Read("test-user", "test-chips@v1").ShouldBe([Line(1), Line(2, "ses_test1")]);
        book.Read("test-user", "demo-mod@v1").ShouldBe([Line(3)]);
        book.Read("other-user", "test-chips@v1").ShouldBe([Line(4)]);
        book.Read("test-user", "nothing@v1").ShouldBeEmpty();
    }

    [Fact]
    public void Keeps_the_last_two_hundred_lines()
    {
        var book = new ModLogBook();

        for (var i = 1; i <= 250; i++)
            book.Add("test-user", "test-chips@v1", Line(i));

        var lines = book.Read("test-user", "test-chips@v1");
        lines.Count.ShouldBe(ModLogBook.Capacity);
        lines[0].ShouldBe(Line(51));
        lines[^1].ShouldBe(Line(250));
    }

    [Fact]
    public void Lines_added_from_many_threads_are_all_kept_up_to_the_cap()
    {
        var book = new ModLogBook();

        Parallel.For(0, 150, i => book.Add("test-user", "test-chips@v1", Line(i)));

        book.Read("test-user", "test-chips@v1").Count.ShouldBe(150);
    }

    [Fact]
    public void A_read_is_a_copy()
    {
        var book = new ModLogBook();
        book.Add("test-user", "test-chips@v1", Line(1));

        var read = book.Read("test-user", "test-chips@v1");
        book.Add("test-user", "test-chips@v1", Line(2));

        read.Count.ShouldBe(1);
    }
}

public sealed class HostModCheckerTests
{
    private readonly FakeModHost _host = new();
    private readonly HostModChecker _checker;

    public HostModCheckerTests() => _checker = new HostModChecker(_host, new TestUserContext("test-user"));

    [Fact]
    public async Task Checks_in_the_current_users_host()
    {
        _host.CheckReport = ModHostTests.Json("""{ "ok": true, "name": "demo-mod" }""");

        var report = await _checker.CheckAsync("/staged/demo-mod");

        report.ShouldNotBeNull().GetProperty("name").GetString().ShouldBe("demo-mod");
        _host.Checks.ShouldBe([("test-user", "/staged/demo-mod")]);
    }

    [Fact]
    public async Task No_host_refuses_the_check_with_the_reason()
    {
        _host.NotReadyReason = "The mod runtime (Bun) isn't installed yet.";

        var thrown = await Should.ThrowAsync<ModStoreException>(() => _checker.CheckAsync("/staged/demo-mod"));

        thrown.Message.ShouldBe("The mod runtime isn't ready yet: The mod runtime (Bun) isn't installed yet.");
    }
}
