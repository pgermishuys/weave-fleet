using WeaveFleet.Infrastructure.Progress;

namespace WeaveFleet.Infrastructure.Tests.Progress;

public sealed class PlanFileReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"plan-reader-{Guid.NewGuid():N}");
    private readonly string _session;
    private readonly string _outside;

    public PlanFileReaderTests()
    {
        _session = Path.Combine(_root, "session");
        _outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(Path.Combine(_session, ".weave", "plans"));
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static string Write(string directory, string relative, string content)
    {
        var path = Path.Combine(directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Reads_a_markdown_file_inside_the_session_folder()
    {
        var path = Write(_session, ".weave/plans/plan.md", "- [ ] One");

        var read = await PlanFileReader.ReadAsync(_session, path, CancellationToken.None);

        (read.Status, read.RelativePath, read.Content).ShouldBe((PlanFileStatus.Read, ".weave/plans/plan.md", "- [ ] One"));
    }

    [Fact]
    public async Task Reads_a_path_relative_to_the_session_folder()
    {
        Write(_session, "TODO.md", "- [x] Done");

        var read = await PlanFileReader.ReadAsync(_session, "TODO.md", CancellationToken.None);

        (read.Status, read.RelativePath).ShouldBe((PlanFileStatus.Read, "TODO.md"));
    }

    [Fact]
    public async Task Refuses_files_outside_the_session_folder()
    {
        var outside = Write(_outside, "plan.md", "- [ ] Secret");

        (await PlanFileReader.ReadAsync(_session, outside, CancellationToken.None)).Status.ShouldBe(PlanFileStatus.Refused);
        (await PlanFileReader.ReadAsync(_session, "../outside/plan.md", CancellationToken.None)).Status.ShouldBe(PlanFileStatus.Refused);
    }

    [Fact]
    public async Task Refuses_a_folder_that_only_shares_a_prefix_with_the_session_folder()
    {
        var sibling = Write(_root, "session-other/plan.md", "- [ ] Not mine");

        (await PlanFileReader.ReadAsync(_session, sibling, CancellationToken.None)).Status.ShouldBe(PlanFileStatus.Refused);
    }

    [Fact]
    public async Task Refuses_a_symlink_that_leads_outside_the_session_folder()
    {
        if (OperatingSystem.IsWindows())
            return;

        var outside = Write(_outside, "plan.md", "- [ ] Secret");
        File.CreateSymbolicLink(Path.Combine(_session, "linked.md"), outside);
        Directory.CreateSymbolicLink(Path.Combine(_session, "linked-dir"), _outside);

        (await PlanFileReader.ReadAsync(_session, "linked.md", CancellationToken.None)).Status.ShouldBe(PlanFileStatus.Refused);
        (await PlanFileReader.ReadAsync(_session, "linked-dir/plan.md", CancellationToken.None)).Status.ShouldBe(PlanFileStatus.Refused);
    }

    [Fact]
    public async Task Follows_a_symlink_that_stays_inside_the_session_folder()
    {
        if (OperatingSystem.IsWindows())
            return;

        Write(_session, ".weave/plans/real.md", "- [ ] One");
        File.CreateSymbolicLink(Path.Combine(_session, "PLAN.md"), Path.Combine(_session, ".weave", "plans", "real.md"));

        var read = await PlanFileReader.ReadAsync(_session, "PLAN.md", CancellationToken.None);

        (read.Status, read.RelativePath).ShouldBe((PlanFileStatus.Read, ".weave/plans/real.md"));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("script.sh")]
    [InlineData("plan.md.bak")]
    public async Task Refuses_files_that_are_not_markdown(string name)
    {
        var path = Write(_session, name, "- [ ] One");

        (await PlanFileReader.ReadAsync(_session, path, CancellationToken.None)).Status.ShouldBe(PlanFileStatus.Refused);
    }

    [Fact]
    public async Task Refuses_a_file_over_the_size_limit()
    {
        var path = Write(_session, "huge.md", new string('x', (int)PlanFileReader.MaxBytes + 1));

        (await PlanFileReader.ReadAsync(_session, path, CancellationToken.None)).Status.ShouldBe(PlanFileStatus.Refused);
    }

    [Fact]
    public async Task Reports_a_missing_file_with_its_relative_path()
    {
        var read = await PlanFileReader.ReadAsync(_session, Path.Combine(_session, "gone.md"), CancellationToken.None);

        (read.Status, read.RelativePath).ShouldBe((PlanFileStatus.Missing, "gone.md"));
    }
}
