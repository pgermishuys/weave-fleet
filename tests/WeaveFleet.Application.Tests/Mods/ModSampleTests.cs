using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Tests.Mods.Host;
using WeaveFleet.Domain.Events;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Mods;

/// <summary>"Add" for a sample Fleet ships: the bundled folder becomes the user's next version, once.</summary>
public sealed class ModSampleTests : IDisposable
{
    private const string User = "test-user";
    private const string Chips = "test-chips";
    private const string Module = "export const register = () => {};\n";

    private readonly string _samples = Path.Combine(Path.GetTempPath(), "fleet-mod-samples-tests", Guid.NewGuid().ToString("n"));
    private readonly InMemoryModVersionStore _store = new();
    private readonly FakeModChecker _checker = new();
    private readonly FakeEventBroadcaster _events = new();
    private readonly FakeFiles _files = new();
    private readonly ModService _service;

    public ModSampleTests()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_samples, Chips)).FullName;
        File.WriteAllText(Path.Combine(folder, "mod.json"), """{"name":"test-chips","version":"0.1.0","description":"Draws test runs","hooks":"./mod.ts"}""");
        File.WriteAllText(Path.Combine(folder, "mod.ts"), Module);
        _files.SamplesFolder = _samples;
        _store.FolderSha256 = Sha256(Module);
        _service = new ModService(_store, _checker, _events, new TestUserContext(User), new ModsSafeMode(), new InMemorySessionRepository(), _files, TimeProvider.System, new FakeModHost(), new ModKeepRequests(TimeProvider.System));
    }

    public void Dispose()
    {
        _store.DeleteFolders();
        Directory.Delete(_samples, recursive: true);
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private string ChangedOnce()
    {
        var sent = _events.Broadcasts.ShouldHaveSingleItem();
        (sent.Topic, sent.Type, sent.UserId).ShouldBe(("sessions", "mods.changed", User));
        var payload = sent.DomainEvent.ShouldBeOfType<ModsChanged>().Payload;
        (payload.Name, payload.SessionId).ShouldBe((Chips, null));
        return payload.Reason;
    }

    [Fact]
    public async Task Adding_keeps_version_1_with_the_check_and_no_session_and_says_so_once()
    {
        _checker.Report = Json("""{"ok":true}""");

        var mod = (await _service.AddSampleAsync(Chips)).Value;

        (mod.Name, mod.Active, mod.Off).ShouldBe((Chips, 1, null));
        var version = mod.Versions.ShouldHaveSingleItem();
        (version.SessionId, version.SessionTitle, version.Note).ShouldBe((null, null, "Fleet's sample"));
        version.Check!.Value.GetProperty("ok").GetBoolean().ShouldBeTrue();
        _store.Staged.Count.ShouldBe(1);
        _checker.Checked.ShouldBe(_store.Staged);
        ChangedOnce().ShouldBe("kept");
    }

    [Fact]
    public async Task Adding_again_changes_nothing_and_says_nothing()
    {
        await _service.AddSampleAsync(Chips);
        _events.Broadcasts.Clear();
        _store.Writes.Clear();

        var again = await _service.AddSampleAsync(Chips);

        again.Value.Versions.ShouldHaveSingleItem();
        _store.Writes.ShouldBeEmpty();
        _events.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Adding_a_sample_that_was_turned_off_turns_it_on_without_a_new_version()
    {
        await _service.AddSampleAsync(Chips);
        await _service.SetOnAsync(Chips, on: false);
        _events.Broadcasts.Clear();

        var mod = (await _service.AddSampleAsync(Chips)).Value;

        (mod.Active, mod.Off).ShouldBe((1, null));
        mod.Versions.ShouldHaveSingleItem();
        ChangedOnce().ShouldBe("kept");
    }

    [Fact]
    public async Task Adding_when_an_older_version_is_the_sample_makes_that_version_active()
    {
        await _service.AddSampleAsync(Chips);
        // A later, different version is active now.
        var history = await _store.GetAsync(User, Chips);
        _store.SeedHistory(User, history with { Active = 2, Versions = [.. history.Versions, history.Versions[0] with { Number = 2, Sha256 = "other" }] });
        _events.Broadcasts.Clear();
        _store.Writes.Clear();

        var mod = (await _service.AddSampleAsync(Chips)).Value;

        mod.Active.ShouldBe(1);
        mod.Versions.Count.ShouldBe(2);
        _store.Writes.ShouldBe([$"use {Chips} 1"]);
        ChangedOnce().ShouldBe("kept");
    }

    [Fact]
    public async Task A_changed_sample_is_kept_as_the_next_version()
    {
        await _service.AddSampleAsync(Chips);
        File.WriteAllText(Path.Combine(_samples, Chips, "mod.ts"), Module + "// 0.2\n");
        _store.FolderSha256 = "new-sha";
        _events.Broadcasts.Clear();

        var mod = (await _service.AddSampleAsync(Chips)).Value;

        (mod.Active, mod.Versions.Count).ShouldBe((2, 2));
        ChangedOnce().ShouldBe("kept");
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("../test-chips")]
    [InlineData("a/b")]
    public async Task A_sample_Fleet_doesnt_ship_is_not_found(string name)
    {
        (await _service.AddSampleAsync(name)).Error.Code.ShouldEndWith(".NotFound");
        _store.Writes.ShouldBeEmpty();
        _events.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_a_samples_folder_every_sample_is_not_found()
    {
        _files.SamplesFolder = null;

        (await _service.AddSampleAsync(Chips)).Error.Code.ShouldEndWith(".NotFound");
        _events.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_report_that_isnt_ok_refuses_and_writes_nothing()
    {
        _checker.Report = Json("""{"ok":false,"errors":[{"message":"imports aren't allowed"}]}""");

        var result = await _service.AddSampleAsync(Chips);

        result.Error.Code.ShouldBe("Validation.Keep");
        result.Error.Description.ShouldContain("imports aren't allowed");
        _store.Writes.ShouldBeEmpty();
        _events.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_host_that_isnt_ready_refuses_as_Keep_does()
    {
        var notReady = new ModService(_store, new RefusingChecker(), _events, new TestUserContext(User), new ModsSafeMode(), new InMemorySessionRepository(), _files, TimeProvider.System, new FakeModHost(), new ModKeepRequests(TimeProvider.System));

        var result = await notReady.AddSampleAsync(Chips);

        result.Error.Code.ShouldBe("Validation.Keep");
        result.Error.Description.ShouldBe("The mod runtime isn't ready yet: no Bun.");
        _events.Broadcasts.ShouldBeEmpty();
    }

    private sealed class RefusingChecker : IModChecker
    {
        public Task<JsonElement?> CheckAsync(string folder, CancellationToken ct = default)
            => throw new ModStoreException("The mod runtime isn't ready yet: no Bun.");
    }
}
