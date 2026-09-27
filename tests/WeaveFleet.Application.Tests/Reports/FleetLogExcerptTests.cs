using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Reports;

namespace WeaveFleet.Application.Tests.Reports;

public sealed class FleetLogExcerptTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fleet-log-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Log = """
        2026-09-27 13:40:00.000 [WRN] [Old] too early
        2026-09-27 13:50:01.000 [DBG] [Hub] session s-123456 subscribed
        2026-09-27 13:50:02.000 [DBG] [Hub] session s-999999 subscribed
        2026-09-27 13:50:03.000 [ERR] [Pump] crashed
        System.InvalidOperationException: nope
           at Pump.Run()
        2026-09-27 13:50:04.000 [INF] [Other] nothing to see
        """;

    [Fact]
    public void Parse_keeps_an_exception_with_the_line_that_logged_it()
    {
        var entries = FleetLogExcerpt.Parse(Log);

        entries.Count.ShouldBe(5);
        entries[3].Level.ShouldBe("ERR");
        entries[3].Text.ShouldBe("2026-09-27 13:50:03.000 [ERR] [Pump] crashed\nSystem.InvalidOperationException: nope\n   at Pump.Run()");
        entries[3].TimeUtc.ShouldBe(new DateTime(2026, 9, 27, 13, 50, 3, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Read_takes_the_window_and_Select_keeps_the_session_and_every_warning_or_error()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "fleet-2026-09-27.log"), Log);
        var location = new FleetLogLocation(true, _dir, "fleet");

        var entries = await FleetLogExcerpt.ReadAsync(
            location,
            new DateTime(2026, 9, 27, 13, 45, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 27, 14, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);
        var excerpt = FleetLogExcerpt.Select(entries, ["s-123456"]);

        excerpt.Included.ShouldBe(2);
        excerpt.LeftOut.ShouldBe(2);
        excerpt.Text.ShouldContain("s-123456 subscribed");
        excerpt.Text.ShouldContain("at Pump.Run()");
        excerpt.Text.ShouldNotContain("s-999999");
        excerpt.Text.ShouldNotContain("too early");
    }

    [Fact]
    public async Task A_window_across_midnight_reads_both_days()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "fleet-2026-09-26.log"), "2026-09-26 23:55:00.000 [WRN] [A] late\n");
        await File.WriteAllTextAsync(Path.Combine(_dir, "fleet-2026-09-27.log"), "2026-09-27 00:05:00.000 [WRN] [B] early\n");

        var entries = await FleetLogExcerpt.ReadAsync(
            new FleetLogLocation(true, _dir, "fleet"),
            new DateTime(2026, 9, 26, 23, 50, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 27, 0, 5, 30, DateTimeKind.Utc),
            CancellationToken.None);

        entries.Select(e => e.Text).ShouldBe(["2026-09-26 23:55:00.000 [WRN] [A] late", "2026-09-27 00:05:00.000 [WRN] [B] early"]);
    }

    [Fact]
    public async Task A_big_file_is_read_from_its_end_and_the_partial_first_line_dropped()
    {
        var path = Path.Combine(_dir, "fleet-2026-09-27.log");
        var line = "2026-09-27 13:50:00.000 [WRN] [Big] " + new string('x', 200) + "\n";
        await File.WriteAllTextAsync(path, string.Concat(Enumerable.Repeat(line, FleetLogExcerpt.MaxBytesPerFile / line.Length + 50)));

        var entries = await FleetLogExcerpt.ReadAsync(
            new FleetLogLocation(true, _dir, "fleet"),
            new DateTime(2026, 9, 27, 13, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 27, 14, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        entries.ShouldAllBe(entry => entry.Text == line.TrimEnd('\n'));
        entries.Count.ShouldBeLessThanOrEqualTo(FleetLogExcerpt.MaxBytesPerFile / line.Length);
    }

    [Fact]
    public void Select_drops_the_oldest_entries_past_the_size_limit()
    {
        var big = new string('y', 1000);
        var entries = Enumerable.Range(0, 300)
            .Select(i => new FleetLogExcerpt.Entry(DateTime.UtcNow, "WRN", $"{i:D3} {big}"))
            .ToList();

        var excerpt = FleetLogExcerpt.Select(entries, []);

        excerpt.Text.Length.ShouldBeLessThanOrEqualTo(FleetLogExcerpt.MaxChars);
        excerpt.Text.ShouldEndWith($"299 {big}\n");
        excerpt.Text.ShouldNotContain($"000 {big}");
    }
}
