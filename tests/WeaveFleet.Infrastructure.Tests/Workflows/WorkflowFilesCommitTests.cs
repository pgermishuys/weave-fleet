using System.Diagnostics;
using Shouldly;
using WeaveFleet.Application.Workflows;

namespace WeaveFleet.Infrastructure.Tests.Workflows;

/// <summary>
/// Fleet commits a step's declared files once they pass the files check, in a real git repository: only the declared
/// paths that are new or changed, never one git ignores, and a failure comes back instead of being thrown.
/// </summary>
public sealed class WorkflowFilesCommitTests : IDisposable
{
    private const string Doc = "docs/design/sheet.md";
    private const string Mockup = "docs/design/sheet.html";

    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"fleet-workflow-commit-{Guid.NewGuid():N}");
    private readonly WorkflowFiles _files = new();

    public WorkflowFilesCommitTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init", "--quiet", "--initial-branch=main");
        Git("config", "user.name", "Fleet Tests");
        Git("config", "user.email", "tests@fleet.invalid");
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.hooksPath", "/dev/null");
        Write("README.md", "# Repo\n");
        Git("add", "README.md");
        Git("commit", "--quiet", "-m", "first");
    }

    [Fact]
    public async Task new_files_are_committed_with_a_message_that_names_the_step_and_the_files()
    {
        Write(Doc, "# Sheet\n");
        Write(Mockup, "<p>Sheet</p>\n");

        var commit = await _files.CommitAsync(_repo, [Doc, Mockup], "Design", CancellationToken.None);

        commit.Error.ShouldBeNull();
        commit.Files.ShouldBe([Doc, Mockup]);
        commit.Commit.ShouldBe(Git("rev-parse", "--short", "HEAD").Trim());
        Git("log", "-1", "--format=%B").TrimEnd().ShouldBe($"docs: design (sheet.md, sheet.html)\n\n{Doc}\n{Mockup}");
        Git("log", "-1", "--format=%an <%ae>").Trim().ShouldBe("Fleet Tests <tests@fleet.invalid>");
        Git("show", "--name-only", "--format=", "HEAD").Trim().Split('\n').ShouldBe([Mockup, Doc], ignoreOrder: true);
        Git("status", "--porcelain").ShouldBeEmpty();
    }

    [Fact]
    public async Task only_the_declared_paths_go_in_whatever_else_is_staged_or_changed()
    {
        Write("src/app.ts", "export {};\n");
        Git("add", "src/app.ts");
        Write("README.md", "# Repo, changed\n");
        Write(Doc, "# Sheet\n");

        var commit = await _files.CommitAsync(_repo, [Doc], "Plan", CancellationToken.None);

        commit.Files.ShouldBe([Doc]);
        Git("show", "--name-only", "--format=", "HEAD").Trim().ShouldBe(Doc);
        var status = Git("status", "--porcelain").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        status.ShouldBe(["A  src/app.ts", " M README.md"], ignoreOrder: true);
    }

    [Fact]
    public async Task a_changed_file_is_committed_and_an_unchanged_one_is_left_out()
    {
        Write(Doc, "# Sheet\n");
        Write(Mockup, "<p>Sheet</p>\n");
        Git("add", "--", Doc, Mockup);
        Git("commit", "--quiet", "-m", "the agent committed them");
        Write(Doc, "# Sheet, as agreed\n");

        var commit = await _files.CommitAsync(_repo, [Doc, Mockup], "Design", CancellationToken.None);

        commit.Files.ShouldBe([Doc]);
        Git("log", "-1", "--format=%s").Trim().ShouldBe("docs: design (sheet.md)");
    }

    [Fact]
    public async Task nothing_to_commit_does_nothing()
    {
        Write(Doc, "# Sheet\n");
        Git("add", "--", Doc);
        Git("commit", "--quiet", "-m", "the agent committed it");
        var head = Git("rev-parse", "HEAD");

        var commit = await _files.CommitAsync(_repo, [Doc], "Plan", CancellationToken.None);

        commit.ShouldBe(WorkflowFilesCommit.Nothing);
        Git("rev-parse", "HEAD").ShouldBe(head);
    }

    [Fact]
    public async Task a_path_git_ignores_stays_out_of_the_commit()
    {
        Write(".gitignore", ".weave/plans/\n");
        Git("add", ".gitignore");
        Git("commit", "--quiet", "-m", "ignore plans");
        Write(".weave/plans/sheet.md", "# Plan\n");
        Write(Doc, "# Sheet\n");

        var commit = await _files.CommitAsync(_repo, [".weave/plans/sheet.md", Doc], "Plan", CancellationToken.None);

        commit.Error.ShouldBeNull();
        commit.Files.ShouldBe([Doc]);
        Git("show", "--name-only", "--format=", "HEAD").Trim().ShouldBe(Doc);
    }

    [Fact]
    public async Task only_an_ignored_path_commits_nothing()
    {
        Write(".gitignore", "notes/\n");
        Git("add", ".gitignore");
        Git("commit", "--quiet", "-m", "ignore notes");
        Write("notes/sheet.md", "# Notes\n");
        var head = Git("rev-parse", "HEAD");

        var commit = await _files.CommitAsync(_repo, ["notes/sheet.md"], "Design", CancellationToken.None);

        commit.ShouldBe(WorkflowFilesCommit.Nothing);
        Git("rev-parse", "HEAD").ShouldBe(head);
    }

    [Fact]
    public async Task a_commit_git_refuses_comes_back_as_its_error()
    {
        // An empty name in the repo's own config wins over any global identity, so git refuses the commit.
        Git("config", "user.name", "");
        Write(Doc, "# Sheet\n");
        var head = Git("rev-parse", "HEAD");

        var commit = await _files.CommitAsync(_repo, [Doc], "Design", CancellationToken.None);

        commit.Commit.ShouldBeNull();
        commit.Error.ShouldNotBeNull().ShouldContain("empty ident name");
        Git("rev-parse", "HEAD").ShouldBe(head);
    }

    [Fact]
    public async Task a_refused_commit_unstages_what_fleet_staged_and_leaves_what_the_agent_staged()
    {
        Git("config", "user.name", "");
        Write(Doc, "# Sheet\n");
        Write(Mockup, "<html></html>\n");
        Write("src/app.ts", "export {};\n");
        Git("add", Mockup, "src/app.ts");

        var commit = await _files.CommitAsync(_repo, [Doc, Mockup], "Design", CancellationToken.None);

        commit.Error.ShouldNotBeNull();
        // The design doc is untracked again, as it was; the mockup and the code stay staged, as the agent left them.
        Git("status", "--porcelain", "--untracked-files=all").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ShouldBe(["A  docs/design/sheet.html", "A  src/app.ts", "?? docs/design/sheet.md"], ignoreOrder: true);
    }

    [Fact]
    public async Task a_folder_that_isnt_a_repository_comes_back_as_an_error()
    {
        var plain = Path.Combine(_repo, "..", $"fleet-not-a-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(plain);
        try
        {
            File.WriteAllText(Path.Combine(plain, "a.md"), "a");
            var commit = await _files.CommitAsync(plain, ["a.md"], "Design", CancellationToken.None);
            commit.Error.ShouldNotBeNull().ShouldContain("not a git repository");
        }
        finally
        {
            Directory.Delete(plain, recursive: true);
        }
    }

    [Fact]
    public void a_long_subject_names_the_step_only()
    {
        var files = new[] { "docs/design/press-see-every-keyboard-shortcut.md", "docs/design/press-see-every-keyboard-shortcut.html" };

        WorkflowFiles.Message("Design", files).ShouldBe($"docs: design\n\n{files[0]}\n{files[1]}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private void Write(string path, string text)
    {
        var full = Path.Combine(_repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private string Git(params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, $"git {string.Join(' ', args)}: {error}");
        return output;
    }
}
