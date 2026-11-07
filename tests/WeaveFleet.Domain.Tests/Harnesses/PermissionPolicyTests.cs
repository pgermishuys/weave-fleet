using Shouldly;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Domain.Tests.Harnesses;

public sealed class PermissionPolicyTests
{
    [Theory]
    [InlineData(PermissionLevels.Ask, PermissionKinds.Read, PermissionReplies.Once)]
    [InlineData(PermissionLevels.Ask, PermissionKinds.Edit, null)]
    [InlineData(PermissionLevels.Ask, PermissionKinds.Shell, null)]
    [InlineData(PermissionLevels.Ask, PermissionKinds.Web, null)]
    [InlineData(PermissionLevels.Ask, PermissionKinds.Other, null)]
    [InlineData(PermissionLevels.Edits, PermissionKinds.Edit, PermissionReplies.Once)]
    [InlineData(PermissionLevels.Edits, PermissionKinds.Shell, null)]
    [InlineData(PermissionLevels.Edits, PermissionKinds.Other, null)]
    [InlineData(PermissionLevels.All, PermissionKinds.Shell, PermissionReplies.Once)]
    [InlineData(PermissionLevels.All, PermissionKinds.Other, PermissionReplies.Once)]
    public void The_level_decides_what_runs_without_asking(string level, string kind, string? expected)
        => new PermissionPolicy(level).Decide(kind).ShouldBe(expected);

    [Fact]
    public void A_run_nobody_watches_refuses_what_would_ask_but_still_allows_what_the_level_allows()
    {
        var policy = new PermissionPolicy(PermissionLevels.Edits, RejectAsks: true);

        policy.Decide(PermissionKinds.Edit).ShouldBe(PermissionReplies.Once);
        policy.Decide(PermissionKinds.Shell).ShouldBe(PermissionReplies.Reject);
    }

    [Theory]
    [InlineData("read", PermissionKinds.Read)]
    [InlineData("Read", PermissionKinds.Read)]
    [InlineData("TodoWrite", PermissionKinds.Read)]
    [InlineData("task", PermissionKinds.Read)]
    [InlineData("fleet_canvas_read", PermissionKinds.Read)]
    [InlineData("edit", PermissionKinds.Edit)]
    [InlineData("Write", PermissionKinds.Edit)]
    [InlineData("MultiEdit", PermissionKinds.Edit)]
    [InlineData("apply_patch", PermissionKinds.Edit)]
    [InlineData("bash", PermissionKinds.Shell)]
    [InlineData("shell", PermissionKinds.Shell)]
    [InlineData("Bash", PermissionKinds.Shell)]
    [InlineData("webfetch", PermissionKinds.Web)]
    [InlineData("WebSearch", PermissionKinds.Web)]
    [InlineData("external_directory", PermissionKinds.Other)]
    [InlineData("github_create_issue", PermissionKinds.Other)]
    [InlineData(null, PermissionKinds.Other)]
    public void Every_harness_names_its_tools_but_they_fall_into_the_same_kinds(string? tool, string kind)
        => PermissionKinds.Classify(tool).ShouldBe(kind);

    [Fact]
    public void What_is_allowed_without_asking_is_only_reading()
        => PermissionKinds.AllowedWithoutAsking.ShouldAllBe(tool => PermissionKinds.Classify(tool) == PermissionKinds.Read);

    private static readonly PermissionPolicy Repo = PermissionPolicy.ReadOnlyIn("/work/orchard");

    [Theory]
    [InlineData("read", "src/app.ts", PermissionReplies.Once)]
    [InlineData("Read", "/work/orchard/src/app.ts", PermissionReplies.Once)]
    [InlineData("read", "/home/user/.bashrc", PermissionReplies.Reject)]
    [InlineData("Read", "/work/orchard-other/x.md", PermissionReplies.Reject)]
    [InlineData("Read", "/work/orchard/../secrets", PermissionReplies.Reject)]
    [InlineData("Glob", "~/.ssh/*", PermissionReplies.Reject)]
    [InlineData("grep", "TODO", PermissionReplies.Once)]
    [InlineData("external_directory", "/etc", PermissionReplies.Reject)]
    [InlineData("fleet_session_read", "abc", PermissionReplies.Once)]
    [InlineData("mcp__fleet__fleet_session_read", "abc", PermissionReplies.Once)]
    [InlineData("fleet_memory_save", "x", PermissionReplies.Reject)]
    [InlineData("mcp__fleet__fleet_memory_save", "x", PermissionReplies.Reject)]
    [InlineData("mcp__fleet__fleet_message", "x", PermissionReplies.Reject)]
    [InlineData("mcp__fleet__fleet_session_start", "x", PermissionReplies.Reject)]
    [InlineData("mcp__fleet__fleet_browser_act", "x", PermissionReplies.Reject)]
    [InlineData("mcp__other__thing", "x", PermissionReplies.Reject)]
    [InlineData("edit", "src/app.ts", PermissionReplies.Reject)]
    [InlineData("Write", "notes.md", PermissionReplies.Reject)]
    [InlineData("webfetch", "https://example.com", PermissionReplies.Reject)]
    [InlineData("bash", "git log --oneline -20", PermissionReplies.Once)]
    [InlineData("Bash", "cat src/app.ts | head -20", PermissionReplies.Once)]
    [InlineData("bash", "git grep -n makeAll", PermissionReplies.Once)]
    [InlineData("bash", "tail -n 20 build.log", PermissionReplies.Once)]
    [InlineData("bash", "grep -rn \"a/b\" src", PermissionReplies.Once)]
    [InlineData("bash", "find . -name '*.cs'", PermissionReplies.Once)]
    [InlineData("bash", "rm -rf build", PermissionReplies.Reject)]
    [InlineData("bash", "git commit -am x", PermissionReplies.Reject)]
    [InlineData("bash", "git log --output=/tmp/x", PermissionReplies.Reject)]
    [InlineData("bash", "git -c core.pager=sh log", PermissionReplies.Reject)]
    [InlineData("bash", "cat a > b", PermissionReplies.Reject)]
    [InlineData("bash", "ls; rm x", PermissionReplies.Reject)]
    [InlineData("bash", "ls && rm x", PermissionReplies.Reject)]
    [InlineData("bash", "echo $(rm x)", PermissionReplies.Reject)]
    [InlineData("bash", "cat x | sh", PermissionReplies.Reject)]
    [InlineData("bash", "find . -name '*.cs' -delete", PermissionReplies.Reject)]
    [InlineData("bash", "find . -exec rm {} +", PermissionReplies.Reject)]
    // find writes a file with any -fprint variant
    [InlineData("bash", "find . -fprint out.txt", PermissionReplies.Reject)]
    [InlineData("bash", "find . -fprint0 out.txt", PermissionReplies.Reject)]
    [InlineData("bash", "find . -fprintf out.txt %p", PermissionReplies.Reject)]
    [InlineData("bash", "find . -fls out.txt", PermissionReplies.Reject)]
    // git grep -O runs a program, also bundled into a short-flag group
    [InlineData("bash", "git grep -Oless foo", PermissionReplies.Reject)]
    [InlineData("bash", "git grep -nOless foo", PermissionReplies.Reject)]
    [InlineData("bash", "git grep --open-files-in-pager=less foo", PermissionReplies.Reject)]
    // rg runs a program
    [InlineData("bash", "rg --pre ./evil foo", PermissionReplies.Reject)]
    [InlineData("bash", "rg --hostname-bin=./evil foo", PermissionReplies.Reject)]
    [InlineData("bash", "rg --hostname-bin ./evil foo", PermissionReplies.Reject)]
    // tail that never ends hangs an unattended run
    [InlineData("bash", "tail -f build.log", PermissionReplies.Reject)]
    [InlineData("bash", "tail -fn 5 build.log", PermissionReplies.Reject)]
    [InlineData("bash", "tail -F build.log", PermissionReplies.Reject)]
    [InlineData("bash", "tail --follow build.log", PermissionReplies.Reject)]
    [InlineData("bash", "tail --retry build.log", PermissionReplies.Reject)]
    // nothing outside the repository (a planted instruction could have it read a secrets file into a note)
    [InlineData("bash", "cat ~/.bashrc", PermissionReplies.Reject)]
    [InlineData("bash", "cat /etc/passwd", PermissionReplies.Reject)]
    [InlineData("bash", "cat ../other/secret.env", PermissionReplies.Reject)]
    [InlineData("bash", "ls .. ", PermissionReplies.Reject)]
    [InlineData("bash", "cat 'a/../../b'", PermissionReplies.Reject)]
    [InlineData("bash", "cat \"/etc/passwd\"", PermissionReplies.Reject)]
    [InlineData("bash", "grep -rn token --include=*.env -f=/etc/hosts", PermissionReplies.Reject)]
    [InlineData("bash", "git log -p -- /etc/passwd", PermissionReplies.Reject)]
    [InlineData("bash", "git diff --no-index a ~/b", PermissionReplies.Reject)]
    [InlineData("bash", "ls C:\\Users", PermissionReplies.Reject)]
    public void A_read_only_run_reads_and_refuses_everything_else(string tool, string pattern, string expected)
        => Repo.DecideReadOnly(tool, [pattern]).ShouldBe(expected);

    [Fact]
    public void A_read_only_run_refuses_a_shell_call_with_no_command_to_check()
        => Repo.DecideReadOnly("bash", []).ShouldBe(PermissionReplies.Reject);

    [Fact]
    public void A_read_only_run_refuses_a_call_when_any_of_its_commands_writes()
        => Repo.DecideReadOnly("bash", ["git log", "touch x"]).ShouldBe(PermissionReplies.Reject);

    [Fact]
    public void With_no_folder_a_read_only_run_reads_nothing_by_absolute_path()
    {
        var policy = PermissionPolicy.ReadOnlyIn(null);

        policy.DecideReadOnly("Read", ["/work/orchard/src/app.ts"]).ShouldBe(PermissionReplies.Reject);
        policy.DecideReadOnly("Read", ["src/app.ts"]).ShouldBe(PermissionReplies.Once);
    }
}
