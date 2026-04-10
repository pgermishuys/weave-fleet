using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Tests.Services;

public sealed class LocalUserContextTests
{
    [Fact]
    public void LocalUserContext_ReturnsExpectedValues()
    {
        var context = new LocalUserContext();

        context.UserId.ShouldBe("local-user");
        context.Email.ShouldBeNull();
        context.DisplayName.ShouldBe("Local User");
        context.IsAuthenticated.ShouldBeTrue();
    }
}
