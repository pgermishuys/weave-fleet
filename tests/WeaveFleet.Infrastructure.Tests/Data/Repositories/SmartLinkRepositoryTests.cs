using Dapper;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class SmartLinkRepositoryTests
{
    private static SmartLink Link(string sessionId, string url, string resourceId, string relationship) => new()
    {
        Id = Guid.NewGuid().ToString(),
        SessionId = sessionId,
        Url = url,
        ProviderId = "github",
        ResourceType = url.Contains("/pull/", StringComparison.Ordinal) ? "pull_request" : "issue",
        ResourceId = resourceId,
        Title = resourceId,
        UserId = TestUserContext.DefaultUserId,
        Relationship = relationship,
    };

    [Fact]
    public async Task InsertDetected_stores_a_new_link_as_pending()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, TestUserContext.DefaultUserId);
        var repo = new SmartLinkRepository(factory, new TestUserContext());

        var stored = await repo.InsertDetectedAsync(
            Link(session.Id, "https://github.com/o/r/pull/1", "o/r#1", SmartLinkRelationships.Mentioned),
            restoreDismissed: false,
            CancellationToken.None);

        stored.ShouldNotBeNull();
        var link = (await repo.ListBySessionIdAsync(session.Id)).ShouldHaveSingleItem();
        link.EnrichmentStatus.ShouldBe(SmartLinkEnrichmentStatuses.Pending);
        link.Relationship.ShouldBe(SmartLinkRelationships.Mentioned);
    }

    [Fact]
    public async Task InsertDetected_matches_issue_and_pull_forms_of_the_same_number_and_only_upgrades()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, TestUserContext.DefaultUserId);
        var repo = new SmartLinkRepository(factory, new TestUserContext());

        await repo.InsertDetectedAsync(Link(session.Id, "https://github.com/o/r/issues/5", "o/r#5", SmartLinkRelationships.Mentioned), false, CancellationToken.None);

        // Same resource via the /pull/ URL: mentioned again is a no-op, own upgrades.
        (await repo.InsertDetectedAsync(Link(session.Id, "https://github.com/o/r/pull/5", "O/R#5", SmartLinkRelationships.Mentioned), false, CancellationToken.None))
            .ShouldBeNull();
        (await repo.InsertDetectedAsync(Link(session.Id, "https://github.com/o/r/pull/5", "o/r#5", SmartLinkRelationships.Own), false, CancellationToken.None))
            .ShouldNotBeNull();

        // A later mention never downgrades.
        (await repo.InsertDetectedAsync(Link(session.Id, "https://github.com/o/r/issues/5", "o/r#5", SmartLinkRelationships.Mentioned), false, CancellationToken.None))
            .ShouldBeNull();

        var link = (await repo.ListBySessionIdAsync(session.Id)).ShouldHaveSingleItem();
        link.Relationship.ShouldBe(SmartLinkRelationships.Own);
    }

    [Fact]
    public async Task InsertDetected_restores_a_dismissed_link_only_when_asked()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, TestUserContext.DefaultUserId);
        var repo = new SmartLinkRepository(factory, new TestUserContext());
        var link = Link(session.Id, "https://github.com/o/r/pull/2", "o/r#2", SmartLinkRelationships.Mentioned);
        await repo.InsertDetectedAsync(link, false, CancellationToken.None);
        await repo.DismissAsync(link.Id);

        (await repo.InsertDetectedAsync(Link(session.Id, link.Url, "o/r#2", SmartLinkRelationships.Mentioned), false, CancellationToken.None))
            .ShouldBeNull();
        (await repo.ListActiveBySessionIdAsync(session.Id)).ShouldBeEmpty();

        await repo.InsertDetectedAsync(Link(session.Id, link.Url, "o/r#2", SmartLinkRelationships.Pinned), true, CancellationToken.None);
        var restored = (await repo.ListActiveBySessionIdAsync(session.Id)).ShouldHaveSingleItem();
        restored.Relationship.ShouldBe(SmartLinkRelationships.Pinned);
    }

    [Fact]
    public async Task InsertDetected_refuses_links_for_another_users_session()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, "someone-else");
        var repo = new SmartLinkRepository(factory, new TestUserContext());

        var stored = await repo.InsertDetectedAsync(
            Link(session.Id, "https://github.com/o/r/pull/1", "o/r#1", SmartLinkRelationships.Mentioned),
            false,
            CancellationToken.None);

        stored.ShouldBeNull();
    }

    [Fact]
    public async Task InsertMissingSourceLinks_adds_the_origin_and_upgrades_an_earlier_mention()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var (workspace, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, TestUserContext.DefaultUserId);
        var (_, _, other) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, TestUserContext.DefaultUserId);
        var repo = new SmartLinkRepository(factory, new TestUserContext());
        var usages = new SessionSourceUsageRepository(factory, new TestUserContext());

        await usages.InsertAsync(new SessionSourceUsage
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = session.Id,
            WorkspaceId = workspace.Id,
            ProviderId = "builtin.github",
            SourceType = "github-issue",
            ActionId = "start-session",
            ResourceUrl = "https://github.com/o/r/issues/42",
            Title = "Add mock API support",
            CreatedAt = DateTime.UtcNow.ToString("O"),
        });
        await usages.InsertAsync(new SessionSourceUsage
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = other.Id,
            WorkspaceId = workspace.Id,
            ProviderId = "builtin.github",
            SourceType = "github-pull-request",
            ActionId = "start-session",
            ResourceUrl = "https://github.com/o/r/pull/9",
            CreatedAt = DateTime.UtcNow.ToString("O"),
        });
        await repo.InsertDetectedAsync(Link(other.Id, "https://github.com/o/r/pull/9", "o/r#9", SmartLinkRelationships.Mentioned), false, CancellationToken.None);
        using (var conn = factory.CreateConnection())
        {
            conn.Execute("UPDATE sessions SET lifecycle_status = 'stopped' WHERE id = @Id", new { session.Id });
            conn.Execute("UPDATE sessions SET lifecycle_status = 'running' WHERE id = @Id", new { other.Id });
        }

        // The background pass covers running sessions; a stopped one is filled in when opened.
        (await repo.InsertMissingSourceLinksAsync(null, CancellationToken.None)).ShouldBe(1);
        (await repo.ListBySessionIdAsync(session.Id)).ShouldBeEmpty();
        (await repo.InsertMissingSourceLinksAsync(session.Id, CancellationToken.None)).ShouldBe(1);
        (await repo.InsertMissingSourceLinksAsync(null, CancellationToken.None)).ShouldBe(0);
        (await repo.InsertMissingSourceLinksAsync(session.Id, CancellationToken.None)).ShouldBe(0);

        var origin = (await repo.ListBySessionIdAsync(session.Id)).ShouldHaveSingleItem();
        origin.Relationship.ShouldBe(SmartLinkRelationships.Origin);
        origin.ResourceId.ShouldBe("o/r#42");
        origin.ResourceType.ShouldBe("issue");
        origin.EnrichmentStatus.ShouldBe(SmartLinkEnrichmentStatuses.Pending);

        (await repo.ListBySessionIdAsync(other.Id)).ShouldHaveSingleItem().Relationship.ShouldBe(SmartLinkRelationships.Origin);
    }

    [Fact]
    public async Task ListDueForEnrichment_returns_pending_and_stale_open_links_on_running_sessions()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var (_, _, running) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, TestUserContext.DefaultUserId);
        var (_, _, stopped) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, TestUserContext.DefaultUserId);
        using (var conn = factory.CreateConnection())
        {
            conn.Execute("UPDATE sessions SET lifecycle_status = 'running' WHERE id = @Id", new { running.Id });
            conn.Execute("UPDATE sessions SET lifecycle_status = 'stopped' WHERE id = @Id", new { stopped.Id });
        }

        var repo = new SmartLinkRepository(factory, new TestUserContext());
        var pending = Link(stopped.Id, "https://github.com/o/r/pull/1", "o/r#1", SmartLinkRelationships.Mentioned);
        var staleRunning = Link(running.Id, "https://github.com/o/r/pull/2", "o/r#2", SmartLinkRelationships.Mentioned);
        var staleStopped = Link(stopped.Id, "https://github.com/o/r/pull/3", "o/r#3", SmartLinkRelationships.Mentioned);
        var fresh = Link(running.Id, "https://github.com/o/r/pull/4", "o/r#4", SmartLinkRelationships.Mentioned);
        foreach (var link in new[] { pending, staleRunning, staleStopped, fresh })
            await repo.InsertDetectedAsync(link, false, CancellationToken.None);

        foreach (var (link, checkedAt) in new[] { (staleRunning, "2026-01-01T00:00:00.0000000Z"), (staleStopped, "2026-01-01T00:00:00.0000000Z"), (fresh, "2026-06-01T00:00:00.0000000Z") })
        {
            link.EnrichmentStatus = SmartLinkEnrichmentStatuses.Resolved;
            link.LastCheckedAt = checkedAt;
            link.UpdatedAt = checkedAt;
            await repo.UpdateEnrichmentAsync(link, CancellationToken.None);
        }

        var due = await repo.ListDueForEnrichmentAsync("2026-03-01T00:00:00.0000000Z", 10, CancellationToken.None);

        due.Select(l => l.Id).ShouldBe([pending.Id, staleRunning.Id], ignoreOrder: true);
    }
}
