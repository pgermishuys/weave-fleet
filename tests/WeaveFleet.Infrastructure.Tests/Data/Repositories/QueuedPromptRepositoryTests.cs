using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

/// <summary>A session's queued messages (<c>queued_prompts</c>), on a real database.</summary>
public sealed class QueuedPromptRepositoryTests
{
    private const string OwnerId = TestUserContext.DefaultUserId;

    private static QueuedPrompt Item(string sessionId, string id, string text, string kind = QueuedPromptKinds.Prompt) => new()
    {
        Id = id,
        SessionId = sessionId,
        Kind = kind,
        Text = text,
        Command = kind == QueuedPromptKinds.Command ? "review" : null,
        Arguments = kind == QueuedPromptKinds.Command ? "src" : null,
        Agent = "reviewer",
        ProviderId = "p",
        ModelId = "m",
        Effort = "high",
        CreatedAt = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task Items_come_back_in_the_order_queued_with_what_was_picked()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var repository = new QueuedPromptRepository(factory, new TestUserContext(OwnerId));

        (await repository.AddAsync(Item(session.Id, "q1", "first"))).ShouldBeTrue();
        (await repository.AddAsync(Item(session.Id, "q2", "/review src", QueuedPromptKinds.Command))).ShouldBeTrue();

        var items = await repository.ListAsync(session.Id);

        items.Select(i => i.Id).ShouldBe(["q1", "q2"]);
        items[1].Kind.ShouldBe(QueuedPromptKinds.Command);
        items[1].Command.ShouldBe("review");
        items[1].Arguments.ShouldBe("src");
        items[0].Agent.ShouldBe("reviewer");
        items[0].Effort.ShouldBe("high");
        items[0].CreatedAt.ShouldBe(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Taking_the_first_takes_each_item_once_in_order()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var repository = new QueuedPromptRepository(factory, new TestUserContext(OwnerId));
        await repository.AddAsync(Item(session.Id, "q1", "first"));
        await repository.AddAsync(Item(session.Id, "q2", "second"));

        (await repository.TakeFirstAsync(session.Id))!.Id.ShouldBe("q1");
        (await repository.TakeFirstAsync(session.Id))!.Id.ShouldBe("q2");
        (await repository.TakeFirstAsync(session.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task An_item_put_back_goes_first_and_one_taken_out_is_gone()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var repository = new QueuedPromptRepository(factory, new TestUserContext(OwnerId));
        await repository.AddAsync(Item(session.Id, "q1", "first"));
        await repository.AddAsync(Item(session.Id, "q2", "second"));
        await repository.AddAsync(Item(session.Id, "q3", "third"));

        var taken = await repository.TakeFirstAsync(session.Id);
        (await repository.ReturnToFrontAsync(taken!)).ShouldBeTrue();
        (await repository.TakeAsync(session.Id, "q2"))!.Text.ShouldBe("second");
        (await repository.TakeAsync(session.Id, "q2")).ShouldBeNull();

        (await repository.ListAsync(session.Id)).Select(i => i.Id).ShouldBe(["q1", "q3"]);
    }

    [Fact]
    public async Task Another_users_session_can_be_neither_queued_to_nor_read_nor_taken_from()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var owner = new QueuedPromptRepository(factory, new TestUserContext(OwnerId));
        var stranger = new QueuedPromptRepository(factory, new TestUserContext("someone-else"));

        (await stranger.AddAsync(Item(session.Id, "theirs", "hi"))).ShouldBeFalse();
        await owner.AddAsync(Item(session.Id, "mine", "hello"));

        (await stranger.ListAsync(session.Id)).ShouldBeEmpty();
        (await stranger.TakeFirstAsync(session.Id)).ShouldBeNull();
        (await stranger.TakeAsync(session.Id, "mine")).ShouldBeNull();
        (await owner.ListAsync(session.Id)).Select(i => i.Id).ShouldBe(["mine"]);
    }
}
