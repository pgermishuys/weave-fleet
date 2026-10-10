using WeaveFleet.Infrastructure.Runtimes;

namespace WeaveFleet.Infrastructure.Tests.Runtimes;

/// <summary>Reading <c>/etc/group</c> for the user's private group, against a fake file (the same on every OS).</summary>
[Trait("Category", "ModsFileSafety")]
public sealed class PrivateGroupTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"fleet-group-{Guid.NewGuid():N}");

    public void Dispose() => File.Delete(_file);

    private bool Is(string contents, uint gid = 1000, uint effective = 1000, string user = "ada")
    {
        File.WriteAllText(_file, contents);
        return PrivateGroup.Is(gid, effective, user, _file);
    }

    [Theory]
    [InlineData("ada:x:1000:")]
    [InlineData("ada:x:1000:ada")]
    [InlineData("root:x:0:\nada:x:1000:\nstaff:x:50:ada,bob")]
    public void The_users_own_group_with_no_other_member_is_private(string contents) => Is(contents).ShouldBeTrue();

    [Theory]
    [InlineData("ada:x:1000:bob")]
    [InlineData("ada:x:1000:ada,bob")]
    public void A_group_with_another_member_is_not_private(string contents) => Is(contents).ShouldBeFalse();

    [Fact]
    public void A_group_not_named_after_the_user_is_not_private() => Is("users:x:1000:").ShouldBeFalse();

    [Fact]
    public void A_group_that_is_not_the_effective_group_is_not_private()
    {
        Is("ada:x:1000:", gid: 1000, effective: 50).ShouldBeFalse();
        Is("ada:x:1000:\nada2:x:1001:", gid: 1001, effective: 1000).ShouldBeFalse();
    }

    [Fact]
    public void A_group_missing_from_the_file_is_not_private() => Is("root:x:0:\nstaff:x:50:").ShouldBeFalse();

    [Theory]
    [InlineData("")]
    [InlineData("ada")]
    [InlineData("ada:x:1000")]
    [InlineData("ada:x:not-a-number:")]
    [InlineData("ada:x:1000:ada:extra")]
    [InlineData("::::")]
    public void Malformed_lines_are_skipped_and_never_count(string contents) => Is(contents).ShouldBeFalse();

    [Fact]
    public void A_malformed_line_does_not_hide_a_good_one() => Is("garbage\nada:x:1000:").ShouldBeTrue();

    [Fact]
    public void A_missing_file_is_not_private()
    {
        PrivateGroup.Is(1000, 1000, "ada", Path.Combine(Path.GetTempPath(), $"fleet-nogroup-{Guid.NewGuid():N}")).ShouldBeFalse();
    }
}
