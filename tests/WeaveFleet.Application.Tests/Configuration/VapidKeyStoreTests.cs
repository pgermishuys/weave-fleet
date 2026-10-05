using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Application.Tests.Configuration;

public sealed class VapidKeyStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fleet-vapid-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Keys_are_made_once_and_kept_beside_the_database()
    {
        var db = Path.Combine(_dir, "fleet.db");

        var first = new VapidKeyStore(db).Get();
        var second = new VapidKeyStore(db).Get();

        second.ShouldBe(first);
        Base64Url.Decode(first.PublicKey).Length.ShouldBe(65);
        Base64Url.Decode(first.PublicKey)[0].ShouldBe((byte)0x04);
        Base64Url.Decode(first.PrivateKey).Length.ShouldBe(32);
        File.Exists(Path.Combine(_dir, "fleet.push.json")).ShouldBeTrue();
    }

    [Fact]
    public void Only_the_owner_can_read_the_key_file()
    {
        if (OperatingSystem.IsWindows())
            return;

        var store = new VapidKeyStore(Path.Combine(_dir, "fleet.db"));
        store.Get();

        File.GetUnixFileMode(store.FilePath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void A_damaged_file_gets_new_keys()
    {
        var db = Path.Combine(_dir, "fleet.db");
        File.WriteAllText(Path.Combine(_dir, "fleet.push.json"), "{ not json");

        var keys = new VapidKeyStore(db).Get();

        Base64Url.Decode(keys.PublicKey).Length.ShouldBe(65);
    }
}
