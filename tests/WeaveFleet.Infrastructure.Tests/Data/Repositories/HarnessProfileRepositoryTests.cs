using Microsoft.Data.Sqlite;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class HarnessProfileRepositoryTests
{
    private static async Task<(SqliteConnection Keeper, HarnessProfileRepository Repo, IDbConnectionFactory Factory)> CreateAsync()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        return (keeper, new HarnessProfileRepository(factory, new TestUserContext()), factory);
    }

    private static HarnessProfile Profile(string name, string harnessType = "opencode") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        HarnessType = harnessType,
        Name = name,
        Content = """{ "model": "fake/model" }""",
        CreatedAt = DateTime.UtcNow.ToString("O"),
        UpdatedAt = DateTime.UtcNow.ToString("O"),
    };

    [Fact]
    public async Task profiles_belong_to_their_owner_and_harness()
    {
        var (keeper, repo, factory) = await CreateAsync();
        using var _ = keeper;
        var work = Profile("Work");
        await repo.InsertAsync(work);
        await repo.InsertAsync(Profile("Other harness", "pi"));

        var listed = await repo.ListAsync("opencode");
        listed.Select(p => p.Name).ShouldBe(["Work"]);
        listed[0].UserId.ShouldBe(TestUserContext.DefaultUserId);
        listed[0].Content.ShouldBe(work.Content);

        var stranger = new HarnessProfileRepository(factory, new TestUserContext("someone-else"));
        (await stranger.ListAsync("opencode")).ShouldBeEmpty();
        (await stranger.GetByIdAsync(work.Id)).ShouldBeNull();
        (await stranger.DeleteAsync(work.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task one_profile_per_harness_is_the_default()
    {
        var (keeper, repo, _) = await CreateAsync();
        using var __ = keeper;
        var work = Profile("Work");
        var local = Profile("Local");
        await repo.InsertAsync(work);
        await repo.InsertAsync(local);

        await repo.SetDefaultAsync("opencode", work.Id);
        (await repo.GetDefaultAsync("opencode"))!.Id.ShouldBe(work.Id);

        await repo.SetDefaultAsync("opencode", local.Id);
        (await repo.GetDefaultAsync("opencode"))!.Id.ShouldBe(local.Id);
        (await repo.ListAsync("opencode")).Count(p => p.IsDefault).ShouldBe(1);

        await repo.SetDefaultAsync("opencode", null);
        (await repo.GetDefaultAsync("opencode")).ShouldBeNull();
    }

    [Fact]
    public async Task update_changes_name_and_content()
    {
        var (keeper, repo, _) = await CreateAsync();
        using var __ = keeper;
        var work = Profile("Work");
        await repo.InsertAsync(work);

        work.Name = "Work (Bedrock)";
        work.Content = """{ "model": "bedrock/model" }""";
        (await repo.UpdateAsync(work)).ShouldBeTrue();

        var stored = await repo.GetByIdAsync(work.Id);
        stored!.Name.ShouldBe("Work (Bedrock)");
        stored.Content.ShouldBe(work.Content);
    }

    [Fact]
    public async Task open_session_counts_leave_out_archived_and_delegated_sessions()
    {
        var (keeper, repo, factory) = await CreateAsync();
        using var __ = keeper;
        var work = Profile("Work");
        await repo.InsertAsync(work);

        var sessions = new SessionRepository(factory, new TestUserContext());
        var (workspace, instance) = await InsertDependenciesAsync(factory);
        Session NewSession(string? profileId, string? parentId = null) => new()
        {
            Id = Guid.NewGuid().ToString(),
            WorkspaceId = workspace.Id,
            InstanceId = instance.Id,
            OpencodeSessionId = Guid.NewGuid().ToString(),
            Title = "Session",
            Status = "active",
            Directory = "/tmp/ws",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            ParentSessionId = parentId,
            HarnessProfileId = profileId,
            UserId = TestUserContext.DefaultUserId,
        };

        var open = NewSession(work.Id);
        var archived = NewSession(work.Id);
        await sessions.InsertAsync(open);
        await sessions.InsertAsync(archived);
        await sessions.InsertAsync(NewSession(work.Id, parentId: open.Id));
        await sessions.InsertAsync(NewSession(profileId: null));
        await sessions.ArchiveAsync(archived.Id, DateTime.UtcNow.ToString("O"));

        var counts = await repo.CountOpenSessionsAsync("opencode");
        counts.ShouldBe(new Dictionary<string, int> { [work.Id] = 1 });
        (await sessions.GetByIdAsync(open.Id))!.HarnessProfileId.ShouldBe(work.Id);
    }

    private static async Task<(Workspace W, Instance I)> InsertDependenciesAsync(IDbConnectionFactory factory)
    {
        var userContext = new TestUserContext();
        var workspace = new Workspace
        {
            Id = Guid.NewGuid().ToString(),
            Directory = "/tmp/ws",
            IsolationStrategy = "existing",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId,
        };
        var instance = new Instance
        {
            Id = Guid.NewGuid().ToString(),
            Port = 9000,
            Directory = "/tmp/ws",
            Url = "http://localhost:9000",
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = TestUserContext.DefaultUserId,
        };
        await new WorkspaceRepository(factory, userContext).InsertAsync(workspace);
        await new InstanceRepository(factory, userContext).InsertAsync(instance);
        return (workspace, instance);
    }
}
