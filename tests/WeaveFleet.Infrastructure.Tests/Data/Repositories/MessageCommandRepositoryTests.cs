using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

/// <summary>The slash commands user messages came from (<c>message_commands</c>), on a real database.</summary>
public sealed class MessageCommandRepositoryTests
{
    private const string OwnerId = TestUserContext.DefaultUserId;

    [Fact]
    public async Task A_saved_command_is_read_back_by_message_id()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var repository = new MessageRepository(factory, new TestUserContext(OwnerId));

        await repository.SaveCommandAsync(session.Id, "msg_command", new SlashCommand("tidy", "src/auth"));
        await repository.SaveCommandAsync(session.Id, "msg_init", new SlashCommand("init", null));

        var commands = await repository.GetCommandsAsync(session.Id);

        commands.Count.ShouldBe(2);
        commands["msg_command"].ShouldBe(new SlashCommand("tidy", "src/auth"));
        commands["msg_init"].ShouldBe(new SlashCommand("init", null));
    }

    [Fact]
    public async Task Saving_the_same_message_again_keeps_the_latest_command()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var repository = new MessageRepository(factory, new TestUserContext(OwnerId));

        await repository.SaveCommandAsync(session.Id, "msg_command", new SlashCommand("tidy", "src/auth"));
        await repository.SaveCommandAsync(session.Id, "msg_command", new SlashCommand("tidy", "src/billing"));

        (await repository.GetCommandsAsync(session.Id)).ShouldHaveSingleItem().Value.ShouldBe(new SlashCommand("tidy", "src/billing"));
    }

    [Fact]
    public async Task Another_users_session_neither_saves_nor_reads_commands()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var owner = new MessageRepository(factory, new TestUserContext(OwnerId));
        var stranger = new MessageRepository(factory, new TestUserContext("someone-else"));

        await stranger.SaveCommandAsync(session.Id, "msg_theirs", new SlashCommand("tidy", null));
        await owner.SaveCommandAsync(session.Id, "msg_mine", new SlashCommand("init", null));

        (await owner.GetCommandsAsync(session.Id)).Keys.ShouldBe(["msg_mine"]);
        (await stranger.GetCommandsAsync(session.Id)).ShouldBeEmpty();
    }
}
