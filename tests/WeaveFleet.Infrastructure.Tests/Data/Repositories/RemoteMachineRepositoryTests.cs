using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

/// <summary>Other machines in this Fleet's list (<c>machines</c>), on a real database.</summary>
public sealed class RemoteMachineRepositoryTests
{
    private static RemoteMachine Mini() => new()
    {
        Id = "machine-mini",
        Name = "mini",
        BaseUrl = "http://127.0.0.1:5582",
        EncryptedToken = "protected",
        Os = "linux",
        AddedAt = new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero),
        Status = RemoteMachineStatuses.Online,
    };

    [Fact]
    public async Task agents_may_hand_work_to_a_machine_only_once_the_owner_allows_it()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var repo = new RemoteMachineRepository(factory);

        await repo.UpsertAsync(Mini());
        (await repo.GetAsync("machine-mini"))!.AgentsAllowed.ShouldBeFalse();

        await repo.UpsertAsync(Mini() with { AgentsAllowed = true });
        (await repo.GetAsync("machine-mini"))!.AgentsAllowed.ShouldBeTrue();
        (await repo.ListAsync()).ShouldHaveSingleItem().AgentsAllowed.ShouldBeTrue();
    }
}
