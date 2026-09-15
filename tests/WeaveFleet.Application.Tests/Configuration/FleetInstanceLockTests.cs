using Shouldly;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Application.Tests.Configuration;

public sealed class FleetInstanceLockTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public FleetInstanceLockTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath(string name = "fleet.db") => Path.Combine(_directory, name);

    [Fact]
    public void A_second_fleet_on_the_same_database_is_refused()
    {
        using var first = FleetInstanceLock.TryAcquire(DatabasePath());

        first.ShouldNotBeNull();
        FleetInstanceLock.TryAcquire(DatabasePath()).ShouldBeNull();
    }

    [Fact]
    public void The_lock_is_free_again_once_released()
    {
        FleetInstanceLock.TryAcquire(DatabasePath())!.Dispose();

        using var second = FleetInstanceLock.TryAcquire(DatabasePath());

        second.ShouldNotBeNull();
    }

    [Fact]
    public void Databases_in_the_same_directory_lock_separately()
    {
        using var first = FleetInstanceLock.TryAcquire(DatabasePath("fleet-a.db"));
        using var second = FleetInstanceLock.TryAcquire(DatabasePath("fleet-b.db"));

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
    }

    [Fact]
    public void The_lock_sits_next_to_the_database_under_its_name()
    {
        FleetInstanceLock.GetLockPath(DatabasePath()).ShouldBe(Path.Combine(_directory, "fleet.lock"));
        FleetInstanceLock.GetInstanceFilePath(DatabasePath()).ShouldBe(Path.Combine(_directory, "fleet.instance.json"));
    }

    [Fact]
    public void Creates_the_data_directory_when_it_is_missing()
    {
        var databasePath = Path.Combine(_directory, "nested", "fleet.db");

        using var instance = FleetInstanceLock.TryAcquire(databasePath);

        instance.ShouldNotBeNull();
        File.Exists(Path.Combine(_directory, "nested", "fleet.lock")).ShouldBeTrue();
    }

    [Fact]
    public void Is_held_only_while_a_fleet_holds_it()
    {
        FleetInstanceLock.IsHeld(DatabasePath()).ShouldBeFalse();

        var instance = FleetInstanceLock.TryAcquire(DatabasePath())!;
        FleetInstanceLock.IsHeld(DatabasePath()).ShouldBeTrue();

        instance.Dispose();
        FleetInstanceLock.IsHeld(DatabasePath()).ShouldBeFalse();
    }

    [Fact]
    public void Probing_does_not_take_the_lock()
    {
        FleetInstanceLock.TryAcquire(DatabasePath())!.Dispose();

        FleetInstanceLock.IsHeld(DatabasePath()).ShouldBeFalse();
        using var instance = FleetInstanceLock.TryAcquire(DatabasePath());

        instance.ShouldNotBeNull();
    }

    [Fact]
    public void The_instance_file_says_who_is_running()
    {
        using var instance = FleetInstanceLock.TryAcquire(DatabasePath())!;
        var startedAt = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

        instance.WriteInstanceFile(new FleetInstanceInfo(4242, "http://127.0.0.1:2113", "0.23.0", DatabasePath(), Desktop: true, startedAt));

        var info = FleetInstanceLock.ReadInstance(DatabasePath());
        info.ShouldBe(new FleetInstanceInfo(4242, "http://127.0.0.1:2113", "0.23.0", DatabasePath(), true, startedAt));
    }

    [Fact]
    public void The_instance_file_uses_camel_case_names()
    {
        using var instance = FleetInstanceLock.TryAcquire(DatabasePath())!;

        instance.WriteInstanceFile(new FleetInstanceInfo(1, "http://127.0.0.1:1", "1.0.0", DatabasePath(), false, DateTimeOffset.UnixEpoch));

        var json = File.ReadAllText(instance.InstanceFilePath);
        json.ShouldContain("\"pid\": 1");
        json.ShouldContain("\"url\": \"http://127.0.0.1:1\"");
        json.ShouldContain("\"desktop\": false");
    }

    [Fact]
    public void Only_the_owner_can_read_the_instance_file()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var instance = FleetInstanceLock.TryAcquire(DatabasePath())!;
        instance.WriteInstanceFile(new FleetInstanceInfo(1, "http://127.0.0.1:1", "1.0.0", DatabasePath(), false, DateTimeOffset.UnixEpoch));

        File.GetUnixFileMode(instance.InstanceFilePath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void Releasing_the_lock_deletes_the_instance_file()
    {
        var instance = FleetInstanceLock.TryAcquire(DatabasePath())!;
        instance.WriteInstanceFile(new FleetInstanceInfo(1, "http://127.0.0.1:1", "1.0.0", DatabasePath(), false, DateTimeOffset.UnixEpoch));

        instance.Dispose();

        File.Exists(instance.InstanceFilePath).ShouldBeFalse();
        FleetInstanceLock.ReadInstance(DatabasePath()).ShouldBeNull();
    }

    [Fact]
    public void A_stale_instance_file_is_removed_by_the_next_fleet()
    {
        File.WriteAllText(FleetInstanceLock.GetInstanceFilePath(DatabasePath()), """{"pid": 99, "url": "http://127.0.0.1:9"}""");

        using var instance = FleetInstanceLock.TryAcquire(DatabasePath());

        File.Exists(FleetInstanceLock.GetInstanceFilePath(DatabasePath())).ShouldBeFalse();
    }

    [Fact]
    public void A_refused_fleet_leaves_the_running_one_s_instance_file_alone()
    {
        using var first = FleetInstanceLock.TryAcquire(DatabasePath())!;
        first.WriteInstanceFile(new FleetInstanceInfo(1, "http://127.0.0.1:1", "1.0.0", DatabasePath(), false, DateTimeOffset.UnixEpoch));

        FleetInstanceLock.TryAcquire(DatabasePath()).ShouldBeNull();

        FleetInstanceLock.ReadInstance(DatabasePath())!.Pid.ShouldBe(1);
    }

    [Fact]
    public void An_unreadable_instance_file_reads_as_none()
    {
        File.WriteAllText(FleetInstanceLock.GetInstanceFilePath(DatabasePath()), "not json");

        FleetInstanceLock.ReadInstance(DatabasePath()).ShouldBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
