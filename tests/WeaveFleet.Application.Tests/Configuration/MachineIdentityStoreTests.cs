using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Application.Tests.Configuration;

public sealed class MachineIdentityStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public MachineIdentityStoreTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath(string name = "fleet.db") => Path.Combine(_directory, name);

    [Fact]
    public void The_identity_lives_next_to_the_database()
    {
        var store = new MachineIdentityStore(DatabasePath());

        store.Get();

        File.Exists(Path.Combine(_directory, "fleet.machine.json")).ShouldBeTrue();
    }

    [Fact]
    public void A_restart_keeps_the_id_and_the_token()
    {
        var first = new MachineIdentityStore(DatabasePath()).Get();

        var second = new MachineIdentityStore(DatabasePath()).Get();

        second.Id.ShouldBe(first.Id);
        second.AccessToken.ShouldBe(first.AccessToken);
    }

    [Fact]
    public void Each_data_directory_is_its_own_machine()
    {
        var a = new MachineIdentityStore(DatabasePath("fleet-a.db")).Get();
        var b = new MachineIdentityStore(DatabasePath("fleet-b.db")).Get();

        a.Id.ShouldNotBe(b.Id);
        a.AccessToken.ShouldNotBe(b.AccessToken);
    }

    [Fact]
    public void A_rename_is_saved()
    {
        new MachineIdentityStore(DatabasePath()).Update(identity => identity with { Name = "hangar" });

        new MachineIdentityStore(DatabasePath()).Get().Name.ShouldBe("hangar");
    }

    [Fact]
    public void A_damaged_file_keeps_its_id_and_gets_a_new_token()
    {
        var id = Guid.NewGuid().ToString("N");
        File.WriteAllText(MachineIdentityStore.GetFilePath(DatabasePath()), $$"""{"id":"{{id}}","accessToken":"short"}""");

        var identity = new MachineIdentityStore(DatabasePath()).Get();

        identity.Id.ShouldBe(id);
        identity.AccessToken!.Length.ShouldBeGreaterThanOrEqualTo(MachineIdentityStore.MinimumTokenLength);
    }

    [Fact]
    public void An_unreadable_file_is_replaced()
    {
        File.WriteAllText(MachineIdentityStore.GetFilePath(DatabasePath()), "not json");

        var identity = new MachineIdentityStore(DatabasePath()).Get();

        Guid.TryParse(identity.Id, out _).ShouldBeTrue();
        identity.AccessToken.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Only_the_owner_can_read_the_file()
    {
        if (OperatingSystem.IsWindows())
            return;

        new MachineIdentityStore(DatabasePath()).Get();

        File.GetUnixFileMode(MachineIdentityStore.GetFilePath(DatabasePath()))
            .ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void Tokens_are_safe_in_a_query_string()
    {
        for (var i = 0; i < 50; i++)
            MachineIdentityStore.NewToken().ShouldMatch("^[A-Za-z0-9_-]{43}$");
    }

    [Fact]
    public void The_saved_token_signs_in_after_a_restart()
    {
        using var environment = new EnvironmentVariableScope("WEAVE_FLEET_AUTH_TOKEN", null);
        var before = new LocalTokenAuthService(new MachineIdentityStore(DatabasePath()));

        var after = new LocalTokenAuthService(new MachineIdentityStore(DatabasePath()));

        after.Source.ShouldBe(LocalTokenSource.Saved);
        after.ValidateToken(before.Token).ShouldBeTrue();
    }

    [Fact]
    public void Replacing_the_token_locks_out_the_old_one_and_survives_a_restart()
    {
        using var environment = new EnvironmentVariableScope("WEAVE_FLEET_AUTH_TOKEN", null);
        var service = new LocalTokenAuthService(new MachineIdentityStore(DatabasePath()));
        var old = service.Token;

        service.TryReplaceToken(out var replacement).ShouldBeTrue();

        service.ValidateToken(old).ShouldBeFalse();
        service.ValidateToken(replacement).ShouldBeTrue();
        new LocalTokenAuthService(new MachineIdentityStore(DatabasePath())).ValidateToken(replacement).ShouldBeTrue();
    }

    [Fact]
    public void The_environment_wins_and_cannot_be_replaced()
    {
        using var environment = new EnvironmentVariableScope("WEAVE_FLEET_AUTH_TOKEN", "fixed-token-0123456789");
        var service = new LocalTokenAuthService(new MachineIdentityStore(DatabasePath()));

        service.Source.ShouldBe(LocalTokenSource.Environment);
        service.Token.ShouldBe("fixed-token-0123456789");
        service.TryReplaceToken(out _).ShouldBeFalse();
        service.ValidateToken("fixed-token-0123456789").ShouldBeTrue();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _original);
    }
}
