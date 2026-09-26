using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Testing.Fakes.Repositories;
using WeaveFleet.Testing.Fixtures;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>Making folders to start sessions in, against real git.</summary>
public sealed class NewFolderServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fleet-new-folder-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"fleet-outside-{Guid.NewGuid():N}");
    private readonly WorkspaceRootService _roots;

    static NewFolderServiceTests()
    {
        // The first commit needs an identity; CI machines have none configured.
        Environment.SetEnvironmentVariable("GIT_AUTHOR_NAME", "Fleet Tests");
        Environment.SetEnvironmentVariable("GIT_AUTHOR_EMAIL", "tests@fleet.invalid");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", "Fleet Tests");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", "tests@fleet.invalid");
    }

    public NewFolderServiceTests()
    {
        Directory.CreateDirectory(_root);
        _roots = new WorkspaceRootService(new InMemoryWorkspaceRootRepository(), new TestUserContext());
        _roots.AddRootAsync(_root).GetAwaiter().GetResult().IsSuccess.ShouldBeTrue();
    }

    public void Dispose()
    {
        foreach (var path in new[] { _root, _outside })
        {
            if (!Directory.Exists(path))
                continue;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
    }

    private NewFolderService Service(bool cloud = false) => new(
        _roots,
        new TestUserContext(),
        new FleetOptions { Cloud = { Enabled = cloud, WorkspaceRoot = cloud ? _root : string.Empty } },
        NullLogger<NewFolderService>.Instance);

    [Fact]
    public async Task Create_WithGit_MakesARepositoryWithAnInitialCommit()
    {
        var path = Path.Combine(_root, "recipe-box");

        var result = await Service().CreateAsync(path, git: true);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
        result.Value.IsGitRepo.ShouldBeTrue();
        result.Value.AddedToFleet.ShouldBeFalse();
        result.Value.Warning.ShouldBeNull();
        Git(path, "log", "-1", "--format=%s").Trim().ShouldBe(NewFolderService.FirstCommitMessage);
    }

    [Fact]
    public async Task Create_WithGit_GivesNewWorktreeSomethingToBranchFrom()
    {
        var path = Path.Combine(_root, "recipe-box");
        await Service().CreateAsync(path, git: true);
        var workspaces = new WorkspaceService(
            new InMemoryWorkspaceRepository(), new TestUserContext(), new FleetOptions(), NullLogger<WorkspaceService>.Instance);

        var worktree = await workspaces.CreateWorkspaceAsync(path, "worktree", "fleet/first-feature");

        worktree.IsSuccess.ShouldBeTrue(worktree.IsFailure ? worktree.Error.Description : null);
    }

    [Fact]
    public async Task Create_WithoutGit_MakesAPlainFolder()
    {
        var path = Path.Combine(_root, "notes");

        var result = await Service().CreateAsync(path, git: false);

        result.IsSuccess.ShouldBeTrue();
        result.Value.IsGitRepo.ShouldBeFalse();
        Directory.Exists(path).ShouldBeTrue();
        Directory.Exists(Path.Combine(path, ".git")).ShouldBeFalse();
    }

    [Fact]
    public async Task Create_MakesMissingParentFolders()
    {
        var path = Path.Combine(_root, "clients", "acme", "site");

        var result = await Service().CreateAsync(path, git: false);

        result.IsSuccess.ShouldBeTrue();
        Directory.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public async Task Create_AnExistingFolder_IsAConflict_AndLeavesItAlone()
    {
        var path = Path.Combine(_root, "weave");
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "keep.txt"), "mine");

        var result = await Service().CreateAsync(path, git: true);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("General.Conflict");
        result.Error.Description.ShouldBe($"{path} already exists.");
        Directory.Exists(Path.Combine(path, ".git")).ShouldBeFalse();
    }

    [Fact]
    public async Task Create_OutsideTheWorkspaceRoots_AddsItToThem()
    {
        var path = Path.Combine(_outside, "side-project");

        var result = await Service().CreateAsync(path, git: false);

        result.IsSuccess.ShouldBeTrue();
        result.Value.AddedToFleet.ShouldBeTrue();
        (await _roots.ListRootsAsync()).Select(root => root.Path).ShouldContain(result.Value.Path);
    }

    [Fact]
    public async Task Create_InCloudMode_IsRefused()
    {
        var result = await Service(cloud: true).CreateAsync(Path.Combine(_root, "recipe-box"), git: true);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldStartWith("Validation.");
        Directory.Exists(Path.Combine(_root, "recipe-box")).ShouldBeFalse();
    }

    [Fact]
    public async Task Clone_CopiesTheRepositoryIn()
    {
        using var source = new RealGitRepository();
        var path = Path.Combine(_root, "copy");

        var result = await Service().CloneFromUrlAsync(source.Path, path, null, null, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
        Git(path, "rev-parse", "HEAD").Trim().ShouldBe(source.CommitOf("HEAD"));
    }

    [Fact]
    public async Task Clone_ThatFails_LeavesNothingBehind()
    {
        var path = Path.Combine(_root, "never");

        var result = await Service().CloneFromUrlAsync(
            Path.Combine(_outside, "no-such-repository"), path, null, null, CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldStartWith("Couldn't clone");
        Directory.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public async Task Clone_OfARepositoryAddress_IsRefusedForAnExistingFolder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "weave"));

        var result = await Service().CloneAsync("pgermishuys/weave", Path.Combine(_root, "weave"), null);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("General.Conflict");
    }

    [Theory]
    [InlineData("pgermishuys/recipe-box", "https://github.com/pgermishuys/recipe-box.git")]
    [InlineData("  pgermishuys/recipe-box/ ", "https://github.com/pgermishuys/recipe-box.git")]
    [InlineData("github.com/pgermishuys/recipe-box", "https://github.com/pgermishuys/recipe-box.git")]
    [InlineData("https://github.com/pgermishuys/recipe-box", "https://github.com/pgermishuys/recipe-box.git")]
    [InlineData("https://github.com/pgermishuys/recipe-box.git", "https://github.com/pgermishuys/recipe-box.git")]
    [InlineData("https://github.com/pgermishuys/recipe-box/tree/main/src", "https://github.com/pgermishuys/recipe-box.git")]
    [InlineData("https://gitlab.com/group/sub/project.git", "https://gitlab.com/group/sub/project.git")]
    [InlineData("ssh://git@example.com/team/repo.git", "ssh://git@example.com/team/repo.git")]
    [InlineData("git@github.com:pgermishuys/recipe-box.git", "git@github.com:pgermishuys/recipe-box.git")]
    public void ResolveCloneUrl_AcceptsGitHubShorthandAndRemoteAddresses(string typed, string expected)
    {
        var result = NewFolderService.ResolveCloneUrl(typed);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("/home/me/src/weave")]
    [InlineData("file:///home/me/src/weave")]
    [InlineData("ext::sh -c touch% /tmp/pwned")]
    [InlineData("http://github.com/pgermishuys/recipe-box")]
    [InlineData("-uhelp")]
    [InlineData("recipe-box")]
    public void ResolveCloneUrl_RefusesLocalPathsAndOtherTransports(string typed)
    {
        NewFolderService.ResolveCloneUrl(typed).IsFailure.ShouldBeTrue();
    }

    [Theory]
    [InlineData("https://github.com/pgermishuys/recipe-box.git", "recipe-box")]
    [InlineData("git@github.com:pgermishuys/recipe-box.git", "recipe-box")]
    [InlineData("https://gitlab.com/group/sub/project", "project")]
    public void FolderNameFor_IsTheRepositoryName(string url, string expected)
    {
        NewFolderService.FolderNameFor(url).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Receiving objects:  38% (380/1000), 1.2 MiB | 2.4 MiB/s", "Receiving objects", 38)]
    [InlineData("Resolving deltas: 100% (52/52), done.", "Resolving deltas", 100)]
    [InlineData("Updating files:   7% (70/1000)", "Updating files", 7)]
    public void ParseProgress_ReadsGitsProgressLines(string line, string phase, int percent)
    {
        NewFolderService.ParseProgress(line).ShouldBe(new CloneProgress(phase, percent));
    }

    [Theory]
    [InlineData("Cloning into '/tmp/x'...")]
    [InlineData("remote: Counting objects: 100% (10/10), done.")]
    public void ParseProgress_IgnoresOtherLines(string line)
    {
        NewFolderService.ParseProgress(line).ShouldBeNull();
    }

    [Fact]
    public void ExpandHome_PutsATildePathInTheHomeFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        WorkspaceRootService.ExpandHome("~/source/recipe-box").ShouldBe(Path.Combine(home, "source/recipe-box"));
        WorkspaceRootService.ExpandHome("~").ShouldBe(home);
        WorkspaceRootService.ExpandHome("/srv/~/x").ShouldBe("/srv/~/x");
        WorkspaceRootService.ExpandHome("~other/x").ShouldBe("~other/x");
    }

    private static string Git(string workingDir, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git") { WorkingDirectory = workingDir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, process.StandardError.ReadToEnd());
        return output;
    }
}
