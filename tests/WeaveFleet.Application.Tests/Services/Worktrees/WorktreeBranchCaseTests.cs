using Shouldly;
using WeaveFleet.Application.Workspaces;

namespace WeaveFleet.Application.Tests.Services.Worktrees;

/// <summary>
/// On macOS and Windows <c>fix/Login</c> and <c>fix/login</c> are one ref and one folder. If Fleet judged them different
/// it would try to check the branch out a second time and git would refuse half way.
/// </summary>
public sealed class WorktreeBranchCaseTests
{
    [Fact]
    public void WhereCaseFolds_ABranchCheckedOutUnderOtherCapitalsCounts()
    {
        var checkedOut = WorkspaceService.BranchSet(["fix/Login"], caseInsensitive: true);

        checkedOut.ShouldContain("fix/login");
        checkedOut.ShouldContain("FIX/LOGIN");
    }

    [Fact]
    public void WhereCaseIsKept_TheyAreTwoBranches()
    {
        var checkedOut = WorkspaceService.BranchSet(["fix/Login"], caseInsensitive: false);

        checkedOut.ShouldNotContain("fix/login");
    }

    [Fact]
    public void ThisMachineFollowsItsFileSystem()
        => WorkspaceService.BranchNamesAreCaseInsensitive.ShouldBe(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());
}
