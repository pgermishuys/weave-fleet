using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

/// <summary>Paired devices (<c>devices</c>), on a real database.</summary>
public sealed class DeviceRepositoryTests
{
    private static readonly DateTimeOffset Paired = new(2026, 10, 5, 9, 30, 0, TimeSpan.Zero);

    private static Device Phone(string id, string name = "Pixel") => new()
    {
        Id = id,
        Name = name,
        Platform = "android",
        TokenHash = [1, 2, 3, 4, 5, 6, 7, 8],
        PairedVia = null,
        CreatedAt = Paired,
        LastUsedAt = Paired,
    };

    [Fact]
    public async Task a_device_round_trips_with_its_hash()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var repo = new DeviceRepository(factory);

        await repo.InsertAsync(Phone("01JDEVICE0000000000000000A") with { PairedVia = "home-machine" });

        var read = await repo.GetAsync("01JDEVICE0000000000000000A");
        read.ShouldNotBeNull();
        read.Name.ShouldBe("Pixel");
        read.Platform.ShouldBe("android");
        read.TokenHash.ShouldBe(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        read.PairedVia.ShouldBe("home-machine");
        read.CreatedAt.ShouldBe(Paired);
        read.LastUsedAt.ShouldBe(Paired);
        read.RevokedAt.ShouldBeNull();
        (await repo.GetAsync("missing")).ShouldBeNull();
    }

    [Fact]
    public async Task touching_moves_last_used_forward()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var repo = new DeviceRepository(factory);
        await repo.InsertAsync(Phone("a"));

        await repo.TouchAsync("a", Paired.AddDays(3));

        (await repo.GetAsync("a"))!.LastUsedAt.ShouldBe(Paired.AddDays(3));
    }

    [Fact]
    public async Task revoking_hides_the_device_from_the_active_list_but_keeps_the_row()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var repo = new DeviceRepository(factory);
        await repo.InsertAsync(Phone("a", "Pixel"));
        await repo.InsertAsync(Phone("b", "iPhone") with { CreatedAt = Paired.AddMinutes(1) });

        (await repo.RevokeAsync("a", Paired.AddHours(1))).ShouldBeTrue();
        (await repo.RevokeAsync("a", Paired.AddHours(2))).ShouldBeFalse();
        (await repo.RevokeAsync("missing", Paired)).ShouldBeFalse();

        (await repo.ListActiveAsync()).Select(d => d.Name).ShouldBe(["iPhone"]);
        (await repo.GetAsync("a"))!.RevokedAt.ShouldBe(Paired.AddHours(1));
    }

    [Fact]
    public async Task a_revoked_device_is_not_touched_back_to_life()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var repo = new DeviceRepository(factory);
        await repo.InsertAsync(Phone("a"));
        await repo.RevokeAsync("a", Paired.AddHours(1));

        await repo.TouchAsync("a", Paired.AddDays(1));

        var read = await repo.GetAsync("a");
        read!.LastUsedAt.ShouldBe(Paired);
        read.RevokedAt.ShouldNotBeNull();
    }
}
