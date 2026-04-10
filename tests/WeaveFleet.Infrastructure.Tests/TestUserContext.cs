using WeaveFleet.Application.Services;

internal sealed class TestUserContext : IUserContext
{
    internal const string DefaultUserId = "test-user";

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
