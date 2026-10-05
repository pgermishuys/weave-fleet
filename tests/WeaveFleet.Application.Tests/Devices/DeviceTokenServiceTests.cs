using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Devices;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Devices;

public sealed class DeviceTokenServiceTests
{
    private readonly InMemoryDeviceRepository _devices = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero));
    private readonly DeviceTokenService _service;

    public DeviceTokenServiceTests()
    {
        _service = new DeviceTokenService(_devices, _time);
    }

    [Fact]
    public async Task An_issued_token_validates_and_only_its_hash_is_kept()
    {
        var (device, token) = await _service.IssueAsync("Pixel", "android");

        token.ShouldStartWith($"fdt_{device.Id}.");
        var validated = await _service.ValidateAsync(token);
        validated.ShouldNotBeNull();
        validated.DeviceId.ShouldBe(device.Id);
        validated.Name.ShouldBe("Pixel");

        var stored = _devices.All.Single();
        var secret = token[(token.IndexOf('.') + 1)..];
        stored.TokenHash.ShouldBe(DeviceToken.Hash(secret));
        System.Text.Encoding.UTF8.GetString(stored.TokenHash).ShouldNotContain(secret);
    }

    [Fact]
    public async Task A_wrong_secret_is_refused()
    {
        var (device, token) = await _service.IssueAsync("Pixel", "android");
        var forged = $"fdt_{device.Id}.{new string('A', 43)}";

        (await _service.ValidateAsync(forged)).ShouldBeNull();
        (await _service.ValidateAsync(token)).ShouldNotBeNull();
    }

    [Fact]
    public async Task An_unknown_device_is_refused()
    {
        var (token, _) = DeviceToken.Create(Ulid.NewUlid().ToString());

        (await _service.ValidateAsync(token)).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("fdt_")]
    [InlineData("fdt_.")]
    [InlineData("fdt_not-a-ulid.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("fdt_01JDEVICE0000000000000000A")]
    [InlineData("fdt_01J9Z3K4M5N6P7Q8R9S0T1V2W3.short")]
    [InlineData("fdt_01J9Z3K4M5N6P7Q8R9S0T1V2W3.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("a-machine-token-0123456789")]
    public async Task Malformed_tokens_fail_without_touching_the_database(string presented)
    {
        (await _service.ValidateAsync(presented)).ShouldBeNull();
        _devices.Reads.ShouldBe(0);
    }

    [Fact]
    public async Task A_removed_device_is_refused_at_once()
    {
        var (device, token) = await _service.IssueAsync("Pixel", "android");
        (await _service.ValidateAsync(token)).ShouldNotBeNull();

        string? revokedId = null;
        _service.Revoked += id => revokedId = id;
        (await _service.RevokeAsync(device.Id)).ShouldBeTrue();

        (await _service.ValidateAsync(token)).ShouldBeNull();
        (await _service.ValidateDeviceAsync(device.Id)).ShouldBeNull();
        (await _service.ListAsync()).ShouldBeEmpty();
        revokedId.ShouldBe(device.Id);
        (await _service.RevokeAsync(device.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_token_unused_for_thirty_days_expires()
    {
        var (device, token) = await _service.IssueAsync("Pixel", "android");

        _time.Advance(TimeSpan.FromDays(30));
        _time.Advance(TimeSpan.FromSeconds(1));

        (await _service.ValidateAsync(token)).ShouldBeNull();
        (await _service.ValidateDeviceAsync(device.Id)).ShouldBeNull();
        (await _service.ListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Using_the_token_moves_its_expiry_forward()
    {
        var (_, token) = await _service.IssueAsync("Pixel", "android");

        _time.Advance(TimeSpan.FromDays(20));
        (await _service.ValidateAsync(token)).ShouldNotBeNull();

        _time.Advance(TimeSpan.FromDays(20));
        (await _service.ValidateAsync(token)).ShouldNotBeNull();

        _time.Advance(TimeSpan.FromDays(30) + TimeSpan.FromSeconds(1));
        (await _service.ValidateAsync(token)).ShouldBeNull();
    }

    [Fact]
    public async Task Last_used_is_written_at_most_every_five_minutes()
    {
        var (device, token) = await _service.IssueAsync("Pixel", "android");

        for (var i = 0; i < 10; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(20));
            (await _service.ValidateAsync(token)).ShouldNotBeNull();
        }

        _devices.Touches.ShouldBe(0);

        _time.Advance(TimeSpan.FromMinutes(2));
        (await _service.ValidateAsync(token)).ShouldNotBeNull();
        _devices.Touches.ShouldBe(1);
        _devices.All.Single(d => d.Id == device.Id).LastUsedAt.ShouldBe(_time.GetUtcNow());

        (await _service.ValidateAsync(token)).ShouldNotBeNull();
        _devices.Touches.ShouldBe(1);
    }

    [Fact]
    public async Task Repeated_checks_are_served_from_the_cache()
    {
        var (_, token) = await _service.IssueAsync("Pixel", "android");

        for (var i = 0; i < 5; i++)
            (await _service.ValidateAsync(token)).ShouldNotBeNull();

        _devices.Reads.ShouldBe(1);

        _time.Advance(DeviceTokenService.CacheFor);
        (await _service.ValidateAsync(token)).ShouldNotBeNull();
        _devices.Reads.ShouldBe(2);
    }

    [Fact]
    public async Task The_list_shows_devices_without_their_hashes()
    {
        await _service.IssueAsync("Pixel", "android", pairedVia: "home");
        _time.Advance(TimeSpan.FromMinutes(1));
        await _service.IssueAsync("iPhone", "ios");

        var listed = await _service.ListAsync();

        listed.Select(d => d.Name).ShouldBe(["Pixel", "iPhone"]);
        listed[0].PairedVia.ShouldBe("home");
        typeof(DeviceSummary).GetProperties().Select(p => p.Name).ShouldNotContain("TokenHash");
    }
}
