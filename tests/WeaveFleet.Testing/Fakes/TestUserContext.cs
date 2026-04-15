using WeaveFleet.Application.Services;

namespace WeaveFleet.Testing.Fakes;

public sealed class TestUserContext : IUserContext
{
    public const string DefaultUserId = "test-user";

    public TestUserContext(string userId = DefaultUserId)
    {
        UserId = userId;
        DisplayName = userId;
    }

    public string UserId { get; }
    public string? Email => null;
    public string? DisplayName { get; }
    public bool IsAuthenticated => true;
}
