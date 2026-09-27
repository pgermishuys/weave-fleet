using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

/// <summary>Turn failures Fleet keeps as messages (<c>messages.error_json</c>), on a real database.</summary>
public sealed class MessageErrorRepositoryTests
{
    private const string OwnerId = TestUserContext.DefaultUserId;

    [Fact]
    public async Task A_kept_turn_failure_is_read_back_with_its_error()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var repository = new MessageRepository(factory, new TestUserContext(OwnerId));
        var error = new TurnError { Name = "ProviderModelNotFoundError", Message = "Model not found", IsRetryable = true };

        await repository.UpsertAsync(MessagePersistenceService.ToPersistedMessage(
            session.Id, MessagePersistenceService.CreateTurnFailureMessage(error, DateTimeOffset.UtcNow)));

        var message = MessagePersistenceService.ToHarnessMessage(
            (await repository.GetBySessionAsync(session.Id, limit: 5, beforeMessageId: null)).ShouldHaveSingleItem());
        message.Role.ShouldBe("assistant");
        message.Parts.ShouldBeEmpty();
        message.Error.ShouldBe(error);
    }
}
