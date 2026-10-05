using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;
using WeaveFleet.Infrastructure.Tests.Data;

namespace WeaveFleet.Infrastructure.Tests.Push;

/// <summary>Push subscriptions (<c>push_subscriptions</c>), on a real database.</summary>
public sealed class PushSubscriptionRepositoryTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private static PushSubscriptionRecord Sub(string id, string endpoint, string? device = "dev-1") => new()
    {
        Id = id,
        DeviceId = device,
        Endpoint = endpoint,
        P256dh = "p256",
        Auth = "auth",
        Kinds = ["permission", "question"],
        QuietWhenDesk = true,
        UserAgent = "Pixel",
        CreatedAt = Created,
    };

    [Fact]
    public async Task A_subscription_round_trips_and_an_endpoint_is_stored_once()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var repo = new PushSubscriptionRepository(factory);

        await repo.UpsertAsync(Sub("a", "https://push.example/1"));
        var updated = await repo.UpsertAsync(Sub("b", "https://push.example/1") with { Kinds = ["finished"], QuietWhenDesk = false, P256dh = "new" });

        updated.Id.ShouldBe("a", "an update keeps the original row");
        var all = await repo.ListAsync();
        all.Count.ShouldBe(1);
        all[0].Kinds.ShouldBe(["finished"]);
        all[0].QuietWhenDesk.ShouldBeFalse();
        all[0].P256dh.ShouldBe("new");
        all[0].Channel.ShouldBe("webpush");
        all[0].CreatedAt.ShouldBe(Created);
        (await repo.GetByEndpointAsync("https://push.example/1"))!.UserAgent.ShouldBe("Pixel");
        (await repo.GetByEndpointAsync("https://push.example/2")).ShouldBeNull();
    }

    [Fact]
    public async Task Failures_count_up_and_a_success_resets_them()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var repo = new PushSubscriptionRepository(factory);
        await repo.UpsertAsync(Sub("a", "https://push.example/1"));

        (await repo.RecordFailureAsync("a")).ShouldBe(1);
        (await repo.RecordFailureAsync("a")).ShouldBe(2);
        await repo.RecordSuccessAsync("a", Created.AddHours(1));

        var read = (await repo.ListAsync()).Single();
        read.FailureCount.ShouldBe(0);
        read.LastSuccessAt.ShouldBe(Created.AddHours(1));
        (await repo.RecordFailureAsync("missing")).ShouldBe(0);
    }

    [Fact]
    public async Task Deleting_by_endpoint_or_by_device()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var repo = new PushSubscriptionRepository(factory);
        await repo.UpsertAsync(Sub("a", "https://push.example/1", "dev-1"));
        await repo.UpsertAsync(Sub("b", "https://push.example/2", "dev-1"));
        await repo.UpsertAsync(Sub("c", "https://push.example/3", "dev-2"));
        await repo.UpsertAsync(Sub("d", "https://push.example/4", null));

        (await repo.DeleteByEndpointAsync("https://push.example/4")).ShouldBeTrue();
        (await repo.DeleteByEndpointAsync("https://push.example/4")).ShouldBeFalse();
        (await repo.DeleteByDeviceAsync("dev-1")).ShouldBe(2);

        (await repo.ListAsync()).Select(s => s.Id).ShouldBe(["c"]);
    }
}
