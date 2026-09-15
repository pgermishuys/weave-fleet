using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class HarnessProfileServiceTests
{
    private readonly FakeHarnessRegistry _registry = new();
    private readonly FakeHarnessRuntime _runtime = new("opencode");
    private readonly InMemorySessionRepository _sessions = new();
    private readonly InMemoryHarnessProfileRepository _profiles;

    public HarnessProfileServiceTests()
    {
        _profiles = new InMemoryHarnessProfileRepository(_sessions);
        _registry.Register(new FakeHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsProfiles = true }));
        _registry.Register(_runtime);
        _registry.Register(new FakeHarness("pi", "Pi"));
    }

    private HarnessProfileService CreateService(FleetOptions? options = null) =>
        new(_profiles, _registry, new TestUserContext("user-1"), options ?? new FleetOptions(), TimeProvider.System);

    [Fact]
    public async Task saving_a_profile_asks_the_harness_to_try_it_first()
    {
        var result = await CreateService().CreateAsync("opencode", "  Work  ", """{ "model": "a/b" }""", CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Name.ShouldBe("Work");
        _runtime.ProfileChecks.ShouldBe(["""{ "model": "a/b" }"""]);
        _profiles.All.Single().UserId.ShouldBe("user-1");
    }

    [Fact]
    public async Task a_profile_the_harness_cant_load_is_not_saved_and_says_why()
    {
        _runtime.ProfileCheckResult = new HarnessProfileCheck(false, "This profile isn't valid JSON.", ["EndOfFileExpected at line 2, column 1"]);

        var result = await CreateService().CreateAsync("opencode", "Work", "{ oops", CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldBe("This profile isn't valid JSON. EndOfFileExpected at line 2, column 1");
        _profiles.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task renaming_without_changing_the_content_skips_the_check()
    {
        var service = CreateService();
        var created = await service.CreateAsync("opencode", "Work", "{}", CancellationToken.None);

        var renamed = await service.UpdateAsync("opencode", created.Value.Id, "Work (Bedrock)", "{}", CancellationToken.None);

        renamed.Value.Name.ShouldBe("Work (Bedrock)");
        _runtime.ProfileChecks.Count.ShouldBe(1);
    }

    [Fact]
    public async Task a_profile_that_open_sessions_use_cant_be_deleted()
    {
        var service = CreateService();
        var created = await service.CreateAsync("opencode", "Work", "{}", CancellationToken.None);
        _sessions.Seed(new Session { Id = "s-1", HarnessType = "opencode", HarnessProfileId = created.Value.Id, RetentionStatus = "active" });

        var result = await service.DeleteAsync("opencode", created.Value.Id);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("General.Conflict");
        result.Error.Description.ShouldBe("1 session uses Work. Archive it before deleting the profile.");
        (await service.ListAsync("opencode")).Value.Single().OpenSessions.ShouldBe(1);
    }

    [Fact]
    public async Task archived_sessions_dont_hold_a_profile()
    {
        var service = CreateService();
        var created = await service.CreateAsync("opencode", "Work", "{}", CancellationToken.None);
        _sessions.Seed(new Session { Id = "s-1", HarnessType = "opencode", HarnessProfileId = created.Value.Id, RetentionStatus = "archived" });

        (await service.DeleteAsync("opencode", created.Value.Id)).IsSuccess.ShouldBeTrue();
        _profiles.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task the_default_can_be_set_and_cleared()
    {
        var service = CreateService();
        var work = await service.CreateAsync("opencode", "Work", "{}", CancellationToken.None);

        (await service.SetDefaultAsync("opencode", work.Value.Id)).IsSuccess.ShouldBeTrue();
        (await service.ListAsync("opencode")).Value.Single().IsDefault.ShouldBeTrue();

        (await service.SetDefaultAsync("opencode", null)).IsSuccess.ShouldBeTrue();
        (await service.ListAsync("opencode")).Value.Single().IsDefault.ShouldBeFalse();

        (await service.SetDefaultAsync("opencode", "missing")).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task empty_names_and_content_are_refused()
    {
        var service = CreateService();

        (await service.CreateAsync("opencode", " ", "{}", CancellationToken.None)).Error.Description.ShouldBe("Give the profile a name.");
        (await service.CreateAsync("opencode", "Work", " ", CancellationToken.None)).Error.Description.ShouldContain("empty");
        _runtime.ProfileChecks.ShouldBeEmpty();
    }

    [Fact]
    public async Task harnesses_without_profiles_and_fleets_with_sign_in_refuse_them()
    {
        (await CreateService().ListAsync("pi")).Error.Description.ShouldBe("Pi doesn't support profiles.");

        var signedIn = CreateService(new FleetOptions { Auth = { Enabled = true } });
        (await signedIn.ListAsync("opencode")).Error.Description.ShouldBe("Profiles aren't available when Fleet runs with sign-in.");
    }
}
