using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

/// <summary>A session's "Don't ask again" rules (<c>session_permission_rules</c>), on a real database.</summary>
public sealed class SessionPermissionRuleRepositoryTests
{
    private const string OwnerId = TestUserContext.DefaultUserId;

    private static SessionPermissionRule Rule(string sessionId, string id, string tool, params string[] patterns) => new()
    {
        Id = id,
        SessionId = sessionId,
        Kind = "shell",
        Tool = tool,
        Patterns = patterns,
        CreatedAt = new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero).AddMinutes(id.Length),
    };

    [Fact]
    public async Task Rules_come_back_oldest_first_with_every_pattern()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var repository = new SessionPermissionRuleRepository(factory);

        (await repository.AddAsync(Rule(session.Id, "r1", "bash", "git status*", "git diff*"))).ShouldBeTrue();
        (await repository.AddAsync(Rule(session.Id, "r22", "WebFetch", "*"))).ShouldBeTrue();

        var rules = await repository.ListAsync(session.Id);

        rules.Select(r => r.Id).ShouldBe(["r1", "r22"]);
        rules[0].Patterns.ShouldBe(["git status*", "git diff*"]);
        rules[0].Tool.ShouldBe("bash");
    }

    [Fact]
    public async Task A_rule_for_a_session_that_doesnt_exist_isnt_saved()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var repository = new SessionPermissionRuleRepository(factory);

        (await repository.AddAsync(Rule("missing", "r1", "bash", "*"))).ShouldBeFalse();
    }

    [Fact]
    public async Task One_rule_or_all_of_them_can_be_removed()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var repository = new SessionPermissionRuleRepository(factory);
        await repository.AddAsync(Rule(session.Id, "r1", "bash", "a*"));
        await repository.AddAsync(Rule(session.Id, "r22", "bash", "b*"));
        await repository.AddAsync(Rule(session.Id, "r333", "bash", "c*"));

        (await repository.DeleteAsync(session.Id, "r1")).ShouldBeTrue();
        (await repository.DeleteAsync(session.Id, "r1")).ShouldBeFalse();
        (await repository.DeleteAsync("other-session", "r22")).ShouldBeFalse();
        (await repository.DeleteAllAsync(session.Id)).ShouldBe(2);

        (await repository.ListAsync(session.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_copy_gets_new_ids_and_the_same_patterns()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var (_, _, other) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var repository = new SessionPermissionRuleRepository(factory);
        await repository.AddAsync(Rule(session.Id, "r1", "bash", "bun test*"));

        await repository.CopyAsync(session.Id, other.Id);

        var copied = (await repository.ListAsync(other.Id)).ShouldHaveSingleItem();
        copied.Id.ShouldNotBe("r1");
        copied.Patterns.ShouldBe(["bun test*"]);
        (await repository.ListAsync(session.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Deleting_the_session_deletes_its_rules()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var (_, _, session) = await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, OwnerId);
        var repository = new SessionPermissionRuleRepository(factory);
        await repository.AddAsync(Rule(session.Id, "r1", "bash", "*"));

        using (var conn = factory.CreateConnection())
        {
            await conn.ExecuteNonQueryAsync("DELETE FROM sessions WHERE id = @Id", cmd => cmd.AddParameter("Id", session.Id));
        }

        (await repository.ListAsync(session.Id)).ShouldBeEmpty();
    }
}
