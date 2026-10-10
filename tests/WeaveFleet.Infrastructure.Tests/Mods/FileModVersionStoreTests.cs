using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WeaveFleet.Application.Mods;
using WeaveFleet.Infrastructure.Mods;

namespace WeaveFleet.Infrastructure.Tests.Mods;

/// <summary>The user's mods on disk: immutable versions, per-session drafts and each mod's store.</summary>
public sealed class FileModVersionStoreTests : IDisposable
{
    private const string User = "local-user";
    private const string Name = "test-chips";
    private const string Session = "ses_alpha";
    private const string Manifest = """{"name":"test-chips","version":"0.1.0","description":"Shows chips.","hooks":"mod.ts"}""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fleet-mods-{Guid.NewGuid():N}");
    private readonly FileModVersionStore _store;

    public FileModVersionStoreTests() => _store = new FileModVersionStore(_root);

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static readonly ModKeepSource NoSource = new(null, null, null);

    private string Draft(string session = Session, string name = Name, string? manifest = Manifest, string code = "export default {};\n")
    {
        var folder = _store.DraftFolder(User, session, name);
        Directory.CreateDirectory(folder);
        if (manifest is not null)
            File.WriteAllText(Path.Combine(folder, "mod.json"), manifest);
        File.WriteAllText(Path.Combine(folder, "mod.ts"), code);
        return folder;
    }

    private string UserFolder() => Directory.GetDirectories(_root).Single();

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task A_mod_with_no_versions_is_empty()
    {
        (await _store.GetAsync(User, Name)).ShouldBe(ModHistory.Empty(Name));
        (await _store.ListAsync(User)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Keep_writes_the_layout_and_an_index_with_the_documented_shape()
    {
        Draft();
        var version = await _store.KeepAsync(User, Session, Name, new ModKeepSource(" Chips ", " first ", Json("""{"ok":true}""")));

        version.Number.ShouldBe(1);
        version.Version.ShouldBe("0.1.0");
        version.SessionId.ShouldBe(Session);
        version.SessionTitle.ShouldBe("Chips");
        version.Note.ShouldBe("first");

        var user = UserFolder();
        Path.GetFileName(user).ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(User)))[..16]);
        File.Exists(Path.Combine(user, Name, "v1", "mod.json")).ShouldBeTrue();
        File.Exists(Path.Combine(user, Name, "v1", "mod.ts")).ShouldBeTrue();
        Directory.GetFileSystemEntries(Path.Combine(user, Name)).Select(Path.GetFileName).Order().ShouldBe(["v1", "versions.json"]);

        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(user, Name, "versions.json")));
        var root = index.RootElement;
        root.GetProperty("name").GetString().ShouldBe(Name);
        root.GetProperty("active").GetInt32().ShouldBe(1);
        root.GetProperty("off").ValueKind.ShouldBe(JsonValueKind.Null);
        var entry = root.GetProperty("versions")[0];
        entry.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["number", "createdAt", "version", "sha256", "sessionId", "sessionTitle", "note", "check"]);
        entry.GetProperty("check").GetProperty("ok").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_version_with_no_check_writes_null()
    {
        Draft();
        await _store.KeepAsync(User, Session, Name, NoSource);

        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(UserFolder(), Name, "versions.json")));
        index.RootElement.GetProperty("versions")[0].GetProperty("check").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_sha256_is_of_the_hooks_module()
    {
        Draft(code: "export default { a: 1 };\n");

        var version = await _store.KeepAsync(User, Session, Name, NoSource);

        version.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("export default { a: 1 };\n"))));
    }

    [Fact]
    public async Task Versions_count_up_and_an_earlier_one_never_changes()
    {
        var draft = Draft(code: "// one\n");
        await _store.KeepAsync(User, Session, Name, NoSource);
        Directory.Exists(draft).ShouldBeFalse();

        Draft(code: "// two\n");
        var second = await _store.KeepAsync(User, Session, Name, NoSource);

        second.Number.ShouldBe(2);
        File.ReadAllText(Path.Combine(_store.VersionFolder(User, Name, 1), "mod.ts")).ShouldBe("// one\n");
        File.ReadAllText(Path.Combine(_store.VersionFolder(User, Name, 2), "mod.ts")).ShouldBe("// two\n");
        var history = await _store.GetAsync(User, Name);
        history.Active.ShouldBe(2);
        history.Versions.Select(v => v.Number).ShouldBe([1, 2]);
        (await _store.ListAsync(User)).Select(h => h.Name).ShouldBe([Name]);
    }

    [Fact]
    public async Task Keep_turns_the_mod_on_and_clears_the_drafts_off_entry()
    {
        Draft();
        await _store.KeepAsync(User, Session, Name, NoSource);
        await _store.SetOffAsync(User, Name, new ModOff(ModOffBy.User, DateTimeOffset.UtcNow));

        Draft(code: "// again\n");
        await _store.SetDraftOffAsync(User, Session, Name, new ModOff(ModOffBy.User, DateTimeOffset.UtcNow));
        await _store.KeepAsync(User, Session, Name, NoSource);

        (await _store.GetAsync(User, Name)).Off.ShouldBeNull();
        (await _store.GetDraftAsync(User, Session, Name)).ShouldBeNull();
        var offFile = Path.Combine(UserFolder(), "drafts", Session, "off.json");
        if (File.Exists(offFile))
            JsonDocument.Parse(File.ReadAllText(offFile)).RootElement.EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    public async Task Keep_without_a_draft_throws()
        => await Should.ThrowAsync<ModStoreException>(() => _store.KeepAsync(User, Session, Name, NoSource));

    [Fact]
    public async Task Keep_refuses_a_draft_without_mod_json()
    {
        Draft(manifest: null);

        var error = await Should.ThrowAsync<ModStoreException>(() => _store.KeepAsync(User, Session, Name, NoSource));

        error.Message.ShouldContain("mod.json");
        (await _store.GetAsync(User, Name)).Versions.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"name":"test-chips","version":"1","description":"d"}""")]
    [InlineData("""{"name":"test-chips","version":"","description":"d","hooks":"mod.ts"}""")]
    [InlineData("""{"name":"other","version":"1","description":"d","hooks":"mod.ts"}""")]
    [InlineData("""{"name":"test-chips","version":"1","description":"d","hooks":"../mod.ts"}""")]
    [InlineData("""{"name":"test-chips","version":"1","description":"d","hooks":"sub/../../mod.ts"}""")]
    [InlineData("""{"name":"test-chips","version":"1","description":"d","hooks":"/etc/passwd.ts"}""")]
    [InlineData("""{"name":"test-chips","version":"1","description":"d","hooks":"missing.ts"}""")]
    [InlineData("""{"name":"test-chips","version":"1","description":"d","hooks":"mod.py"}""")]
    public async Task Keep_refuses_a_manifest_that_is_wrong(string manifest)
    {
        var folder = Draft(manifest: manifest);
        File.WriteAllText(Path.Combine(folder, "mod.py"), "x");

        var error = await Should.ThrowAsync<ModStoreException>(() => _store.KeepAsync(User, Session, Name, NoSource));

        error.Message.ShouldNotBeNullOrWhiteSpace();
        (await _store.GetAsync(User, Name)).Versions.ShouldBeEmpty();
        Directory.Exists(folder).ShouldBeTrue();
    }

    [Theory]
    [InlineData("mod.js")]
    [InlineData("mod.mjs")]
    [InlineData("src/main.mts")]
    public async Task Keep_accepts_each_module_extension(string hooks)
    {
        var folder = Draft(manifest: Manifest.Replace("mod.ts", hooks));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, hooks))!);
        File.WriteAllText(Path.Combine(folder, hooks), "export default {};");

        (await _store.KeepAsync(User, Session, Name, NoSource)).Number.ShouldBe(1);
    }

    [Fact]
    public async Task Keep_refuses_a_symbolic_link()
    {
        var folder = Draft();
        Directory.CreateDirectory(Path.Combine(folder, "pages"));
        File.CreateSymbolicLink(Path.Combine(folder, "pages", "secret.txt"), "/etc/hostname");

        var error = await Should.ThrowAsync<ModStoreException>(() => _store.KeepAsync(User, Session, Name, NoSource));

        error.Message.ShouldContain("link", Case.Insensitive);
    }

    [Fact]
    public async Task Keep_refuses_too_many_files()
    {
        var folder = Draft();
        for (var i = 0; i < 500; i++)
            File.WriteAllText(Path.Combine(folder, $"f{i}.txt"), "x");

        await Should.ThrowAsync<ModStoreException>(() => _store.KeepAsync(User, Session, Name, NoSource));
    }

    [Fact]
    public async Task Keep_refuses_too_many_bytes()
    {
        var folder = Draft();
        File.WriteAllBytes(Path.Combine(folder, "big.bin"), new byte[16 * 1024 * 1024]);

        await Should.ThrowAsync<ModStoreException>(() => _store.KeepAsync(User, Session, Name, NoSource));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("fleet-x")]
    [InlineData("drafts")]
    [InlineData("Upper")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task A_bad_name_throws_on_writes_and_reads_as_nothing(string name)
    {
        Should.Throw<ArgumentException>(() => _store.VersionFolder(User, name, 1));
        Should.Throw<ArgumentException>(() => _store.DraftFolder(User, Session, name));
        await Should.ThrowAsync<ArgumentException>(() => _store.KeepAsync(User, Session, name, NoSource));
        await Should.ThrowAsync<ArgumentException>(() => _store.SetActiveAsync(User, name, 1));
        await Should.ThrowAsync<ArgumentException>(() => _store.SetOffAsync(User, name, null));
        await Should.ThrowAsync<ArgumentException>(() => _store.SetDraftOffAsync(User, Session, name, null));
        await Should.ThrowAsync<ArgumentException>(() => _store.SetValueAsync(User, name, "k", Json("1")));
        await Should.ThrowAsync<ArgumentException>(() => _store.DeleteValueAsync(User, name, "k"));

        (await _store.GetAsync(User, name)).Versions.ShouldBeEmpty();
        (await _store.GetDraftAsync(User, Session, name)).ShouldBeNull();
        (await _store.ReadVersionFilesAsync(User, name, 1)).ShouldBeNull();
        (await _store.ReadDraftFilesAsync(User, Session, name)).ShouldBeNull();
        (await _store.GetValueAsync(User, name, "k")).ShouldBeNull();
        (await _store.KeysAsync(User, name)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_name_of_65_characters_is_refused()
    {
        var name = new string('a', 65);
        await Should.ThrowAsync<ArgumentException>(() => _store.KeepAsync(User, Session, name, NoSource));
        (await _store.GetAsync(User, name)).Versions.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task A_bad_session_id_throws_on_writes_and_reads_as_nothing(string session)
    {
        Should.Throw<ArgumentException>(() => _store.DraftFolder(User, session, Name));
        await Should.ThrowAsync<ArgumentException>(() => _store.KeepAsync(User, session, Name, NoSource));
        (await _store.ListDraftsAsync(User, session)).ShouldBeEmpty();
        (await _store.GetDraftAsync(User, session, Name)).ShouldBeNull();
    }

    [Fact]
    public async Task List_leaves_out_store_only_and_draft_folders()
    {
        Draft(name: "zeta-mod", manifest: Manifest.Replace("test-chips", "zeta-mod"));
        Draft(name: "alpha-mod", manifest: Manifest.Replace("test-chips", "alpha-mod"));
        await _store.KeepAsync(User, Session, "zeta-mod", NoSource);
        await _store.KeepAsync(User, Session, "alpha-mod", NoSource);
        await _store.SetValueAsync(User, "only-store", "k", Json("1"));
        Draft(name: Name);

        (await _store.ListAsync(User)).Select(h => h.Name).ShouldBe(["alpha-mod", "zeta-mod"]);
    }

    [Fact]
    public async Task A_corrupt_index_reads_as_empty()
    {
        Draft();
        await _store.KeepAsync(User, Session, Name, NoSource);
        File.WriteAllText(Path.Combine(UserFolder(), Name, "versions.json"), "{ nope");

        (await _store.GetAsync(User, Name)).ShouldBe(ModHistory.Empty(Name));
    }

    [Fact]
    public async Task The_active_version_can_change_and_must_exist()
    {
        Draft();
        await _store.KeepAsync(User, Session, Name, NoSource);
        Draft();
        await _store.KeepAsync(User, Session, Name, NoSource);

        await _store.SetActiveAsync(User, Name, 1);
        (await _store.GetAsync(User, Name)).ActiveVersion!.Number.ShouldBe(1);

        await Should.ThrowAsync<ModStoreException>(() => _store.SetActiveAsync(User, Name, 9));
        (await _store.GetAsync(User, Name)).Active.ShouldBe(1);
    }

    [Fact]
    public async Task A_mod_can_be_turned_off_and_on()
    {
        Draft();
        await _store.KeepAsync(User, Session, Name, NoSource);
        var at = new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero);

        await _store.SetOffAsync(User, Name, new ModOff(ModOffBy.Strikes, at, "boom"));
        (await _store.GetAsync(User, Name)).Off.ShouldBe(new ModOff(ModOffBy.Strikes, at, "boom"));

        await _store.SetOffAsync(User, Name, null);
        (await _store.GetAsync(User, Name)).Off.ShouldBeNull();
    }

    [Fact]
    public async Task Turning_off_a_mod_with_no_versions_throws()
        => await Should.ThrowAsync<ModStoreException>(() => _store.SetOffAsync(User, Name, new ModOff(ModOffBy.User, DateTimeOffset.UtcNow)));

    [Fact]
    public async Task Drafts_are_listed_per_session_with_their_off_state()
    {
        Draft(session: "ses_alpha", name: "beta-mod", manifest: Manifest.Replace("test-chips", "beta-mod"));
        Draft(session: "ses_alpha", name: "alpha-mod", manifest: Manifest.Replace("test-chips", "alpha-mod"));
        Draft(session: "ses_beta", name: "other-mod", manifest: Manifest.Replace("test-chips", "other-mod"));
        var at = new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero);
        await _store.SetDraftOffAsync(User, "ses_alpha", "beta-mod", new ModOff(ModOffBy.User, at));

        var drafts = await _store.ListDraftsAsync(User, "ses_alpha");

        drafts.Select(d => d.Name).ShouldBe(["alpha-mod", "beta-mod"]);
        drafts[0].Off.ShouldBeNull();
        drafts[1].Off.ShouldBe(new ModOff(ModOffBy.User, at));
        drafts[0].SessionId.ShouldBe("ses_alpha");
        drafts[0].Folder.ShouldBe(_store.DraftFolder(User, "ses_alpha", "alpha-mod"));
        (await _store.ListDraftsAsync(User, "ses_beta")).Select(d => d.Name).ShouldBe(["other-mod"]);
        (await _store.ListDraftsAsync(User, "ses_none")).ShouldBeEmpty();
        (await _store.GetDraftAsync(User, "ses_beta", "beta-mod")).ShouldBeNull();
        (await _store.GetDraftAsync(User, "ses_alpha", "beta-mod"))!.Off.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_draft_off_state_persists_and_clears()
    {
        Draft();
        await _store.SetDraftOffAsync(User, Session, Name, new ModOff(ModOffBy.User, DateTimeOffset.UtcNow));
        using (var store2 = new FileModVersionStore(_root))
            (await store2.GetDraftAsync(User, Session, Name))!.Off!.By.ShouldBe("user");

        await _store.SetDraftOffAsync(User, Session, Name, null);
        (await _store.GetDraftAsync(User, Session, Name))!.Off.ShouldBeNull();
    }

    [Fact]
    public async Task Turning_off_a_missing_draft_throws_and_turning_it_on_does_nothing()
    {
        await Should.ThrowAsync<ModStoreException>(() => _store.SetDraftOffAsync(User, Session, Name, new ModOff(ModOffBy.User, DateTimeOffset.UtcNow)));
        await _store.SetDraftOffAsync(User, Session, Name, null);
    }

    [Fact]
    public async Task Files_are_read_as_text_with_relative_paths()
    {
        var folder = Draft();
        Directory.CreateDirectory(Path.Combine(folder, "pages", "deep"));
        File.WriteAllText(Path.Combine(folder, "pages", "deep", "a.html"), "<p>hi</p>");
        File.WriteAllBytes(Path.Combine(folder, "icon.bin"), [0xff, 0xfe, 0x00, 0x80]);
        File.WriteAllText(Path.Combine(folder, "huge.txt"), new string('x', ModStoreLimits.ShownFileBytes + 1));
        File.CreateSymbolicLink(Path.Combine(folder, "link.txt"), "/etc/hostname");

        var draftFiles = await _store.ReadDraftFilesAsync(User, Session, Name);

        draftFiles!.Select(f => f.Path).ShouldBe(["mod.json", "mod.ts", "pages/deep/a.html"]);
        draftFiles![2].Content.ShouldBe("<p>hi</p>");

        File.Delete(Path.Combine(folder, "link.txt"));
        await _store.KeepAsync(User, Session, Name, NoSource);
        var versionFiles = await _store.ReadVersionFilesAsync(User, Name, 1);
        versionFiles!.Select(f => f.Path).ShouldBe(["mod.json", "mod.ts", "pages/deep/a.html"]);
        (await _store.ReadVersionFilesAsync(User, Name, 2)).ShouldBeNull();
        (await _store.ReadDraftFilesAsync(User, Session, Name)).ShouldBeNull();
    }

    [Fact]
    public async Task The_manifest_is_read_or_null()
    {
        var folder = Draft();
        var manifest = await _store.ReadManifestAsync(folder);
        manifest.ShouldBe(new ModManifest(Name, "0.1.0", "Shows chips.", "mod.ts"));

        (await _store.ReadManifestAsync(Path.Combine(_root, "nowhere"))).ShouldBeNull();
        File.WriteAllText(Path.Combine(folder, "mod.json"), "nope");
        (await _store.ReadManifestAsync(folder)).ShouldBeNull();
        File.WriteAllText(Path.Combine(folder, "mod.json"), """{"name":"x","version":"1","description":"","hooks":"a.ts"}""");
        (await _store.ReadManifestAsync(folder)).ShouldBeNull();
        File.WriteAllText(Path.Combine(folder, "mod.json"), """{"name":"x","version":"1","description":"d","hooks":5}""");
        (await _store.ReadManifestAsync(folder)).ShouldBeNull();
    }

    [Fact]
    public async Task The_store_gets_sets_deletes_and_lists_keys()
    {
        (await _store.GetValueAsync(User, Name, "a")).ShouldBeNull();
        await _store.SetValueAsync(User, Name, "b", Json("""{"n":2}"""));
        await _store.SetValueAsync(User, Name, "a", Json("\"one\""));

        (await _store.GetValueAsync(User, Name, "a"))!.Value.GetString().ShouldBe("one");
        (await _store.GetValueAsync(User, Name, "b"))!.Value.GetProperty("n").GetInt32().ShouldBe(2);
        (await _store.KeysAsync(User, Name)).ShouldBe(["a", "b"]);

        await _store.DeleteValueAsync(User, Name, "a");
        await _store.DeleteValueAsync(User, Name, "missing");
        (await _store.KeysAsync(User, Name)).ShouldBe(["b"]);
        File.ReadAllText(Path.Combine(UserFolder(), Name, "store.json")).ShouldBe("""{"b":{"n":2}}""");
    }

    [Fact]
    public async Task A_key_must_be_1_to_1024_characters()
    {
        await Should.ThrowAsync<ArgumentException>(() => _store.SetValueAsync(User, Name, "", Json("1")));
        await Should.ThrowAsync<ArgumentException>(() => _store.SetValueAsync(User, Name, new string('k', 1025), Json("1")));
        await _store.SetValueAsync(User, Name, new string('k', 1024), Json("1"));
    }

    [Fact]
    public async Task A_draft_and_the_kept_mod_share_one_store()
    {
        Draft();
        await _store.SetValueAsync(User, Name, "seen", Json("3"));

        await _store.KeepAsync(User, Session, Name, NoSource);

        (await _store.GetValueAsync(User, Name, "seen"))!.Value.GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task A_set_past_the_limit_throws_and_leaves_the_store_as_it_was()
    {
        await _store.SetValueAsync(User, Name, "small", Json("1"));
        var path = Path.Combine(UserFolder(), Name, "store.json");
        var before = File.ReadAllText(path);

        var error = await Should.ThrowAsync<ModStoreFullException>(
            () => _store.SetValueAsync(User, Name, "big", Json($"\"{new string('x', ModStoreLimits.StoreBytes)}\"")));

        error.Message.ShouldContain(Name);
        error.Message.ShouldContain("4 MiB");
        File.ReadAllText(path).ShouldBe(before);
    }

    [Fact]
    public async Task A_set_that_fits_exactly_passes()
    {
        // {"k":"<x>"} is 8 bytes around the string.
        var fill = ModStoreLimits.StoreBytes - 8;
        await _store.SetValueAsync(User, Name, "k", Json($"\"{new string('x', fill)}\""));
        new FileInfo(Path.Combine(UserFolder(), Name, "store.json")).Length.ShouldBe(ModStoreLimits.StoreBytes);

        await Should.ThrowAsync<ModStoreFullException>(() => _store.SetValueAsync(User, Name, "k", Json($"\"{new string('x', fill + 1)}\"")));
    }

    [Fact]
    public async Task A_corrupt_store_reads_as_empty_and_is_moved_aside_on_the_next_write()
    {
        await _store.SetValueAsync(User, Name, "a", Json("1"));
        var folder = Path.Combine(UserFolder(), Name);
        File.WriteAllText(Path.Combine(folder, "store.json"), "{ broken");

        (await _store.KeysAsync(User, Name)).ShouldBeEmpty();
        (await _store.GetValueAsync(User, Name, "a")).ShouldBeNull();

        await _store.SetValueAsync(User, Name, "b", Json("2"));

        (await _store.KeysAsync(User, Name)).ShouldBe(["b"]);
        var aside = Directory.GetFiles(folder, "store.json.broken-*").ShouldHaveSingleItem();
        File.ReadAllText(aside).ShouldBe("{ broken");
    }

    [Fact]
    public async Task Twenty_sessions_keeping_the_same_name_get_versions_one_to_twenty()
    {
        for (var i = 0; i < 20; i++)
            Draft(session: $"ses_{i}", code: $"// {i}\n");

        var versions = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => _store.KeepAsync(User, $"ses_{i}", Name, NoSource))));

        versions.Select(v => v.Number).Order().ShouldBe(Enumerable.Range(1, 20));
        var history = await _store.GetAsync(User, Name);
        history.Versions.Select(v => v.Number).ShouldBe(Enumerable.Range(1, 20));
        for (var n = 1; n <= 20; n++)
            Directory.Exists(_store.VersionFolder(User, Name, n)).ShouldBeTrue();
        Directory.GetDirectories(Path.Combine(UserFolder(), Name)).Length.ShouldBe(20);
    }

    [Fact]
    public async Task Parallel_sets_of_different_keys_all_land()
    {
        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() => _store.SetValueAsync(User, Name, $"k{i}", Json($"{i}")))));

        (await _store.KeysAsync(User, Name)).Count.ShouldBe(30);
    }
}
