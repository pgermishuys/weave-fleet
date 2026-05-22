using System.Diagnostics;
using Shouldly;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Application.Tests.Services;

public sealed class GitDiffServiceTests
{
    [Fact]
    public async Task capture_baseline_creates_stable_ref_from_supplied_baseline_id()
    {
        var runner = new RecordingGitRunner(
            new GitCommandResult(0, "/repo/root\n", string.Empty),
            new GitCommandResult(0, "abc123\n", string.Empty),
            new GitCommandResult(0, string.Empty, string.Empty));
        var service = new GitDiffService(runner);

        var result = await service.CaptureBaselineAsync("/repo/root/sub", "session/123", CancellationToken.None);

        result.ShouldNotBeNull();
        result.RefName.ShouldBe("refs/fleet/baselines/session-123");
        result.RepoRoot.ShouldBe("/repo/root");
        runner.Calls[0].Arguments.ShouldBe(["rev-parse", "--show-toplevel"]);
        runner.Calls[1].WorkingDirectory.ShouldBe("/repo/root");
        runner.Calls[1].Arguments.ShouldBe(["stash", "create", "fleet baseline session/123"]);
        runner.Calls[2].Arguments.ShouldBe(["update-ref", "refs/fleet/baselines/session-123", "abc123"]);
    }

    [Fact]
    public async Task capture_baseline_uses_head_when_stash_create_has_no_changes()
    {
        var runner = new RecordingGitRunner(
            new GitCommandResult(0, "/repo/root\n", string.Empty),
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(0, "head123\n", string.Empty),
            new GitCommandResult(0, string.Empty, string.Empty));
        var service = new GitDiffService(runner);

        var result = await service.CaptureBaselineAsync("/repo/root", "session-123", CancellationToken.None);

        result.ShouldNotBeNull();
        result.RefName.ShouldBe("refs/fleet/baselines/session-123");
        runner.Calls[2].Arguments.ShouldBe(["rev-parse", "HEAD"]);
        runner.Calls[3].Arguments.ShouldBe(["update-ref", "refs/fleet/baselines/session-123", "head123"]);
    }

    [Fact]
    public async Task capture_baseline_returns_null_when_git_fails()
    {
        var runner = new RecordingGitRunner(new GitCommandResult(128, string.Empty, "not a git repo"));
        var service = new GitDiffService(runner);

        var result = await service.CaptureBaselineAsync("/repo/root", "session-123", CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task capture_baseline_returns_null_in_real_non_git_directory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"weave-git-diff-non-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var service = new GitDiffService();

        try
        {
            var result = await service.CaptureBaselineAsync(tempRoot, "manual-non-git-check", CancellationToken.None);

            result.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task compute_diffs_reports_unavailable_when_git_fails()
    {
        var runner = new RecordingGitRunner(new GitCommandResult(128, string.Empty, "bad ref"));
        var service = new GitDiffService(runner);

        var result = await service.ComputeDiffsWithAvailabilityAsync("/repo/root", "refs/fleet/baselines/session-123", "src", CancellationToken.None);

        result.Available.ShouldBeFalse();
        result.Diffs.ShouldBeEmpty();
    }

    [Fact]
    public async Task compute_diffs_reports_available_when_git_succeeds_with_no_changes()
    {
        var runner = new RecordingGitRunner(
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(0, string.Empty, string.Empty));
        var service = new GitDiffService(runner);

        var result = await service.ComputeDiffsWithAvailabilityAsync("/repo/root", "refs/fleet/baselines/session-123", "src", CancellationToken.None);

        result.Available.ShouldBeTrue();
        result.Diffs.ShouldBeEmpty();
    }

    [Fact]
    public async Task compute_diffs_runs_numstat_and_untracked_queries_for_prefix()
    {
        var runner = new RecordingGitRunner(
            new GitCommandResult(0, "3\t1\tsrc/changed.cs\n-\t-\tsrc/image.bin\n", string.Empty),
            new GitCommandResult(0, "M\tsrc/changed.cs\nM\tsrc/image.bin\n", string.Empty),
            new GitCommandResult(0, "src/new.cs\n", string.Empty));
        var service = new GitDiffService(runner);

        var result = await service.ComputeDiffsWithAvailabilityAsync("/repo/root", "refs/fleet/baselines/session-123", "/src/", CancellationToken.None);

        runner.Calls[0].Arguments.ShouldBe(["diff", "--numstat", "refs/fleet/baselines/session-123", "--", "src"]);
        runner.Calls[1].Arguments.ShouldBe(["diff", "--name-status", "refs/fleet/baselines/session-123", "--", "src"]);
        runner.Calls[2].Arguments.ShouldBe(["ls-files", "--others", "--exclude-standard", "--", "src"]);
        result.Available.ShouldBeTrue();
        result.Diffs.Count.ShouldBe(3);
        result.Diffs[0].ShouldBe(new FileDiffSummary("src/changed.cs", 3, 1, IsBinary: false, IsUntracked: false) { Status = "modified" });
        result.Diffs[1].ShouldBe(new FileDiffSummary("src/image.bin", null, null, IsBinary: true, IsUntracked: false) { Status = "modified" });
        result.Diffs[2].ShouldBe(new FileDiffSummary("src/new.cs", null, 0, IsBinary: false, IsUntracked: true) { Status = "added" });
    }

    [Fact]
    public async Task compute_diffs_uses_name_status_for_deleted_files_even_when_path_exists()
    {
        var runner = new RecordingGitRunner(
            new GitCommandResult(0, "0\t2\tsrc/deleted.cs\n", string.Empty),
            new GitCommandResult(0, "D\tsrc/deleted.cs\n", string.Empty),
            new GitCommandResult(0, string.Empty, string.Empty));
        var service = new GitDiffService(runner);

        var result = await service.ComputeDiffsWithAvailabilityAsync("/repo/root", "refs/fleet/baselines/session-123", "src", CancellationToken.None);

        result.Available.ShouldBeTrue();
        result.Diffs.ShouldBe([
            new FileDiffSummary("src/deleted.cs", 0, 2, IsBinary: false, IsUntracked: false) { Status = "deleted" }
        ]);
    }

    [Fact]
    public async Task compute_diffs_counts_added_lines_for_untracked_text_files()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"weave-git-diff-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "src"));
        await File.WriteAllTextAsync(Path.Combine(tempRoot, "src", "new.cs"), "one\ntwo\nthree");
        var runner = new RecordingGitRunner(
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(0, "src/new.cs\n", string.Empty));
        var service = new GitDiffService(runner);

        try
        {
            var result = await service.ComputeDiffsWithAvailabilityAsync(tempRoot, "refs/fleet/baselines/session-123", "src", CancellationToken.None);

            result.Available.ShouldBeTrue();
            result.Diffs.ShouldBe([
                new FileDiffSummary("src/new.cs", 3, 0, IsBinary: false, IsUntracked: true) { Status = "added" }
            ]);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task real_git_repo_reports_tracked_and_untracked_diffs_after_baseline_and_excludes_ignored_files()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"weave-git-diff-real-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var service = new GitDiffService();

        try
        {
            await RunGitAsync(tempRoot, "init");
            await File.WriteAllTextAsync(Path.Combine(tempRoot, ".gitignore"), "ignored.log\n");
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "tracked.txt"), "alpha\nbeta\n");
            await RunGitAsync(tempRoot, "add", ".gitignore", "tracked.txt");
            await RunGitAsync(tempRoot, "-c", "user.name=Weave Test", "-c", "user.email=weave@example.invalid", "commit", "-m", "initial");

            var baseline = await service.CaptureBaselineAsync(tempRoot, "manual-check", CancellationToken.None);

            baseline.ShouldNotBeNull();
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "tracked.txt"), "alpha\nbeta changed\ngamma\n");
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "new.txt"), "one\ntwo\nthree");
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "ignored.log"), "should not appear\n");

            var result = await service.ComputeDiffsWithAvailabilityAsync(baseline.RepoRoot, baseline.RefName, string.Empty, CancellationToken.None);

            result.Available.ShouldBeTrue();
            result.Diffs.ShouldBe([
                new FileDiffSummary("new.txt", 3, 0, IsBinary: false, IsUntracked: true) { Status = "added" },
                new FileDiffSummary("tracked.txt", 2, 1, IsBinary: false, IsUntracked: false) { Status = "modified" }
            ]);
            result.Diffs.ShouldNotContain(summary => summary.Path == "ignored.log");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void parse_diffs_keeps_renamed_destination_path()
    {
        var result = GitDiffService.ParseDiffs("1\t2\told.cs\tnew.cs\n", "R100\told.cs\tnew.cs\n", string.Empty);

        result.ShouldBe([new FileDiffSummary("new.cs", 1, 2, IsBinary: false, IsUntracked: false) { Status = "modified" }]);
    }

    [Fact]
    public async Task compute_diffs_with_content_sets_added_before_empty_and_after_working_tree()
    {
        var tempRoot = CreateTempDirectory();
        var service = new GitDiffService(new RecordingGitRunner(
            new GitCommandResult(0, "1\t0\tadded.cs\n", string.Empty),
            new GitCommandResult(0, "A\tadded.cs\n", string.Empty),
            new GitCommandResult(0, string.Empty, string.Empty)));

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "added.cs"), "new\n");

            var result = await service.ComputeDiffsWithContentAsync(tempRoot, "baseline", string.Empty, CancellationToken.None);

            result.ShouldHaveSingleItem().ShouldBe(new FileDiffContent(
                "added.cs",
                Before: string.Empty,
                After: "new\n",
                IsBinary: false,
                IsTruncated: false,
                Additions: 1,
                Deletions: 0,
                Status: "added"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task compute_diffs_with_content_sets_untracked_before_empty_and_after_working_tree()
    {
        var tempRoot = CreateTempDirectory();
        var service = new GitDiffService(new RecordingGitRunner(
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(0, "untracked.cs\n", string.Empty)));

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "untracked.cs"), "new\n");

            var result = await service.ComputeDiffsWithContentAsync(tempRoot, "baseline", string.Empty, CancellationToken.None);

            var content = result.ShouldHaveSingleItem();

            content.Path.ShouldBe("untracked.cs");
            content.Before.ShouldBe(string.Empty);
            content.After.ShouldBe("new\n");
            content.IsBinary.ShouldBeFalse();
            content.IsTruncated.ShouldBeFalse();
            content.Status.ShouldBe("added");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task compute_diffs_with_content_sets_deleted_after_empty_and_before_baseline()
    {
        var service = new GitDiffService(new RecordingGitRunner(
            new GitCommandResult(0, "0\t1\tdeleted.cs\n", string.Empty),
            new GitCommandResult(0, "D\tdeleted.cs\n", string.Empty),
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(0, "old\n", string.Empty)));

        var result = await service.ComputeDiffsWithContentAsync("/repo/root", "baseline", string.Empty, CancellationToken.None);

        result.ShouldHaveSingleItem().ShouldBe(new FileDiffContent(
            "deleted.cs",
            Before: "old\n",
            After: string.Empty,
            IsBinary: false,
            IsTruncated: false,
            Additions: 0,
            Deletions: 1,
            Status: "deleted"));
    }

    [Fact]
    public async Task compute_diffs_with_content_sets_modified_before_baseline_and_after_working_tree()
    {
        var tempRoot = CreateTempDirectory();
        var service = new GitDiffService(new RecordingGitRunner(
            new GitCommandResult(0, "1\t1\tmodified.cs\n", string.Empty),
            new GitCommandResult(0, "M\tmodified.cs\n", string.Empty),
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(0, "old\n", string.Empty)));

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "modified.cs"), "new\n");

            var result = await service.ComputeDiffsWithContentAsync(tempRoot, "baseline", string.Empty, CancellationToken.None);

            result.ShouldHaveSingleItem().ShouldBe(new FileDiffContent(
                "modified.cs",
                Before: "old\n",
                After: "new\n",
                IsBinary: false,
                IsTruncated: false,
                Additions: 1,
                Deletions: 1,
                Status: "modified"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task compute_diffs_with_content_sets_binary_before_and_after_empty_with_binary_flag()
    {
        var service = new GitDiffService(new RecordingGitRunner(
            new GitCommandResult(0, "-\t-\timage.bin\n", string.Empty),
            new GitCommandResult(0, "M\timage.bin\n", string.Empty),
            new GitCommandResult(0, string.Empty, string.Empty)));

        var result = await service.ComputeDiffsWithContentAsync("/repo/root", "baseline", string.Empty, CancellationToken.None);

        result.ShouldHaveSingleItem().ShouldBe(new FileDiffContent(
            "image.bin",
            Before: string.Empty,
            After: string.Empty,
            IsBinary: true,
            IsTruncated: false,
            Additions: 0,
            Deletions: 0,
            Status: "modified"));
    }

    [Fact]
    public async Task compute_diffs_with_content_detects_untracked_binary_content()
    {
        var tempRoot = CreateTempDirectory();
        var service = new GitDiffService(new RecordingGitRunner(
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(0, "image.bin\n", string.Empty)));

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(tempRoot, "image.bin"), [1, 0, 2]);

            var result = await service.ComputeDiffsWithContentAsync(tempRoot, "baseline", string.Empty, CancellationToken.None);
            var content = result.ShouldHaveSingleItem();

            content.Before.ShouldBe(string.Empty);
            content.After.ShouldBe(string.Empty);
            content.IsBinary.ShouldBeTrue();
            content.IsTruncated.ShouldBeFalse();
            content.Status.ShouldBe("added");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task compute_diffs_with_content_sets_oversized_before_and_after_empty_with_truncated_flag()
    {
        var tempRoot = CreateTempDirectory();
        var service = new GitDiffService(new RecordingGitRunner(
            new GitCommandResult(0, "1\t0\tlarge.txt\n", string.Empty),
            new GitCommandResult(0, "A\tlarge.txt\n", string.Empty),
            new GitCommandResult(0, string.Empty, string.Empty)));

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(tempRoot, "large.txt"), Enumerable.Repeat((byte)'a', (512 * 1024) + 1).ToArray());

            var result = await service.ComputeDiffsWithContentAsync(tempRoot, "baseline", string.Empty, CancellationToken.None);
            var content = result.ShouldHaveSingleItem();

            content.Before.ShouldBe(string.Empty);
            content.After.ShouldBe(string.Empty);
            content.IsBinary.ShouldBeFalse();
            content.IsTruncated.ShouldBeTrue();
            content.Status.ShouldBe("added");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task get_file_content_returns_git_show_output_for_baseline_ref()
    {
        var runner = new RecordingGitRunner(new GitCommandResult(0, "baseline content\n", string.Empty));
        var service = new GitDiffService(runner);

        var result = await service.GetFileContentAsync("/repo/root", "baseline", "src/file.cs", CancellationToken.None);

        result.ShouldBe("baseline content\n");
        var call = runner.Calls.ShouldHaveSingleItem();
        call.WorkingDirectory.ShouldBe("/repo/root");
        call.Arguments.ShouldBe(["show", "baseline:src/file.cs"]);
    }

    [Fact]
    public async Task get_file_content_maps_failed_git_show_to_null()
    {
        var runner = new RecordingGitRunner(new GitCommandResult(128, string.Empty, "fatal: path does not exist"));
        var service = new GitDiffService(runner);

        var result = await service.GetFileContentAsync("/repo/root", "baseline", "missing.cs", CancellationToken.None);

        result.ShouldBeNull();
        runner.Calls.ShouldHaveSingleItem().Arguments.ShouldBe(["show", "baseline:missing.cs"]);
    }

    [Fact]
    public async Task get_file_content_maps_oversized_git_show_output_to_null()
    {
        var oversizedContent = new string('a', (512 * 1024) + 1);
        var runner = new RecordingGitRunner(new GitCommandResult(0, oversizedContent, string.Empty));
        var service = new GitDiffService(runner);

        var result = await service.GetFileContentAsync("/repo/root", "baseline", "large.txt", CancellationToken.None);

        result.ShouldBeNull();
        runner.Calls.ShouldHaveSingleItem().Arguments.ShouldBe(["show", "baseline:large.txt"]);
    }

    [Fact]
    public async Task get_file_content_maps_binary_git_show_output_to_null()
    {
        var runner = new RecordingGitRunner(new GitCommandResult(0, "text\0binary", string.Empty));
        var service = new GitDiffService(runner);

        var result = await service.GetFileContentAsync("/repo/root", "baseline", "image.bin", CancellationToken.None);

        result.ShouldBeNull();
        runner.Calls.ShouldHaveSingleItem().Arguments.ShouldBe(["show", "baseline:image.bin"]);
    }

    [Fact]
    public async Task get_file_content_reads_working_tree_file_when_ref_is_null()
    {
        var tempRoot = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(tempRoot, "src"));
        await File.WriteAllTextAsync(Path.Combine(tempRoot, "src", "file.cs"), "working tree\n");
        var runner = new RecordingGitRunner();
        var service = new GitDiffService(runner);

        try
        {
            var result = await service.GetFileContentAsync(tempRoot, null, "src/file.cs", CancellationToken.None);

            result.ShouldBe("working tree\n");
            runner.Calls.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task get_file_content_maps_missing_working_tree_file_to_null()
    {
        var tempRoot = CreateTempDirectory();
        var runner = new RecordingGitRunner();
        var service = new GitDiffService(runner);

        try
        {
            var result = await service.GetFileContentAsync(tempRoot, null, "missing.cs", CancellationToken.None);

            result.ShouldBeNull();
            runner.Calls.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task get_file_content_maps_oversized_working_tree_file_to_null()
    {
        var tempRoot = CreateTempDirectory();
        await File.WriteAllBytesAsync(Path.Combine(tempRoot, "large.txt"), Enumerable.Repeat((byte)'a', (512 * 1024) + 1).ToArray());
        var runner = new RecordingGitRunner();
        var service = new GitDiffService(runner);

        try
        {
            var result = await service.GetFileContentAsync(tempRoot, null, "large.txt", CancellationToken.None);

            result.ShouldBeNull();
            runner.Calls.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task get_file_content_maps_binary_working_tree_file_to_null()
    {
        var tempRoot = CreateTempDirectory();
        await File.WriteAllBytesAsync(Path.Combine(tempRoot, "image.bin"), [1, 0, 2]);
        var runner = new RecordingGitRunner();
        var service = new GitDiffService(runner);

        try
        {
            var result = await service.GetFileContentAsync(tempRoot, null, "image.bin", CancellationToken.None);

            result.ShouldBeNull();
            runner.Calls.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task get_file_content_rejects_path_traversal_before_git_show()
    {
        var tempRoot = CreateTempDirectory();
        var runner = new RecordingGitRunner(new GitCommandResult(0, "secret", string.Empty));
        var service = new GitDiffService(runner);

        try
        {
            var result = await service.GetFileContentAsync(tempRoot, "baseline", "../secret.txt", CancellationToken.None);

            result.ShouldBeNull();
            runner.Calls.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.ShouldBe(0, $"git {string.Join(' ', arguments)} failed. stdout: {standardOutput} stderr: {standardError}");
    }

    private static string CreateTempDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"weave-git-diff-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    private sealed class RecordingGitRunner(params GitCommandResult[] results) : IGitDiffCommandRunner
    {
        private readonly Queue<GitCommandResult> _results = new(results);

        public List<GitCall> Calls { get; } = [];

        public Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            Calls.Add(new GitCall(workingDirectory, [.. arguments]));
            var result = _results.Count == 0
                ? new GitCommandResult(0, string.Empty, string.Empty)
                : _results.Dequeue();

            return Task.FromResult(result);
        }
    }

    private sealed record GitCall(string WorkingDirectory, IReadOnlyList<string> Arguments);
}
