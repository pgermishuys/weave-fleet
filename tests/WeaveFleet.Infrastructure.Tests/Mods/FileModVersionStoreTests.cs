using System.Diagnostics;
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
            Remove(_root);
        foreach (var folder in _outside)
            if (Directory.Exists(folder))
                Remove(folder);
    }

    private static void Remove(string folder)
    {
        // Kept versions are read-only, and a test may have made a folder read-only on purpose.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(folder, (UnixFileMode)0b111_111_111);
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
                entry.Delete();
            else if (entry is DirectoryInfo directory)
                Remove(directory.FullName);
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
        }

        Directory.Delete(folder);
    }

    private readonly List<string> _outside = [];

    /// <summary>A folder beside the store root, with one file in it, for a link to point at.</summary>
    private string Outside()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"fleet-mods-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "secret.txt"), "outside");
        File.WriteAllText(Path.Combine(folder, "mod.json"), Manifest);
        File.WriteAllText(Path.Combine(folder, "mod.ts"), "export default {};");
        _outside.Add(folder);
        return folder;
    }

    private static readonly ModKeepSource NoSource = new(null, null);
    private static readonly ModKeepCheck NoCheck = (_, _) => Task.FromResult<JsonElement?>(null);

    private static ModKeepCheck Report(string json) => (_, _) => Task.FromResult<JsonElement?>(Json(json));

    private Task<ModVersion> Keep(string session = Session, string name = Name, ModKeepCheck? check = null)
        => _store.KeepAsync(User, session, name, NoSource, check ?? NoCheck);

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
        var version = await _store.KeepAsync(User, Session, Name, new ModKeepSource(" Chips ", " first "), Report("""{"ok":true}"""));

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
        await _store.KeepAsync(User, Session, Name, NoSource, NoCheck);

        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(UserFolder(), Name, "versions.json")));
        index.RootElement.GetProperty("versions")[0].GetProperty("check").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_sha256_is_of_the_hooks_module()
    {
        Draft(code: "export default { a: 1 };\n");

        var version = await _store.KeepAsync(User, Session, Name, NoSource, NoCheck);

        version.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("export default { a: 1 };\n"))));
    }

    [Fact]
    public async Task Versions_count_up_and_an_earlier_one_never_changes()
    {
        var draft = Draft(code: "// one\n");
        await _store.KeepAsync(User, Session, Name, NoSource, NoCheck);
        Directory.Exists(draft).ShouldBeFalse();
        // The session's drafts folder goes too when that was its last draft.
        Directory.Exists(Path.GetDirectoryName(draft)).ShouldBeFalse();

        Draft(code: "// two\n");
        var second = await _store.KeepAsync(User, Session, Name, NoSource, NoCheck);

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
        await _store.KeepAsync(User, Session, Name, NoSource, NoCheck);
        await _store.SetOffAsync(User, Name, new ModOff(ModOffBy.User, DateTimeOffset.UtcNow));

        Draft(code: "// again\n");
        await _store.SetDraftOffAsync(User, Session, Name, new ModOff(ModOffBy.User, DateTimeOffset.UtcNow));
        await _store.KeepAsync(User, Session, Name, NoSource, NoCheck);

        (await _store.GetAsync(User, Name)).Off.ShouldBeNull();
        (await _store.GetDraftAsync(User, Session, Name)).ShouldBeNull();
        var offFile = Path.Combine(UserFolder(), "drafts", Session, "off.json");
        if (File.Exists(offFile))
            JsonDocument.Parse(File.ReadAllText(offFile)).RootElement.EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    public async Task Keep_without_a_draft_throws()
        => await Should.ThrowAsync<ModStoreException>(() => _store.KeepAsync(User, Session, Name, NoSource, NoCheck));

    [Fact]
    public async Task Keep_refuses_a_draft_without_mod_json()
    {
        Draft(manifest: null);

        var error = await Should.ThrowAsync<ModStoreException>(() => _store.KeepAsync(User, Session, Name, NoSource, NoCheck));

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

        var error = await Should.ThrowAsync<ModStoreException>(() => _store.KeepAsync(User, Session, Name, NoSource, NoCheck));

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

        (await _store.KeepAsync(User, Session, Name, NoSource, NoCheck)).Number.ShouldBe(1);
    }

    [Trait("Category", "ModsFileSafety")]
    [Fact]
    public async Task Keep_refuses_a_symbolic_link()
    {
        var folder = Draft();
        Directory.CreateDirectory(Path.Combine(folder, "pages"));
        File.CreateSymbolicLink(Path.Combine(folder, "pages", "secret.txt"), "/etc/hostname");

        var error = await Should.ThrowAsync<ModStoreException>(() => _store.KeepAsync(User, Session, Name, NoSource, NoCheck));

        error.Message.ShouldContain("link", Case.Insensitive);
    }

    [Fact]
    public async Task Keep_refuses_too_many_files()
    {
        var folder = Draft();
        for (var i = 0; i < 500; i++)
            File.WriteAllText(Path.Combine(folder, $"f{i}.txt"), "x");

        await Should.ThrowAsync<ModStoreException>(() => _store.KeepAsync(User, Session, Name, NoSource, NoCheck));
    }

    [Fact]
    public async Task Keep_refuses_too_many_bytes()
    {
        var folder = Draft();
        File.WriteAllBytes(Path.Combine(folder, "big.bin"), new byte[16 * 1024 * 1024]);

        await Should.ThrowAsync<ModStoreException>(() => _store.KeepAsync(User, Session, Name, NoSource, NoCheck));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("fleet-x")]
    [InlineData("drafts")]
    [InlineData("Upper")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("con")]
    [InlineData("nul")]
    [InlineData("aux")]
    [InlineData("prn")]
    [InlineData("com1")]
    [InlineData("lpt9")]
    public async Task A_bad_name_throws_on_writes_and_reads_as_nothing(string name)
    {
        Should.Throw<ArgumentException>(() => _store.VersionFolder(User, name, 1));
        Should.Throw<ArgumentException>(() => _store.DraftFolder(User, Session, name));
        await Should.ThrowAsync<ArgumentException>(() => _store.KeepAsync(User, Session, name, NoSource, NoCheck));
        await Should.ThrowAsync<ArgumentException>(() => _store.UseVersionAsync(User, name, 1));
        await Should.ThrowAsync<ArgumentException>(() => _store.SetOffAsync(User, name, null));
        await Should.ThrowAsync<ArgumentException>(() => _store.SetDraftOffAsync(User, Session, name, null));
        await Should.ThrowAsync<ArgumentException>(() => _store.SetValueAsync(User, name, "k", Json("1")));
        await Should.ThrowAsync<ArgumentException>(() => _store.DeleteValueAsync(User, name, "k"));

        (await _store.GetAsync(User, name)).Versions.ShouldBeEmpty();
        (await _store.GetDraftAsync(User, Session, name)).ShouldBeNull();
        (await _store.ReadVersionFilesAsync(User, name, 1)).ShouldBeNull();
        (await _store.ReadVersionManifestAsync(User, name, 1)).ShouldBeNull();
        await Should.ThrowAsync<ArgumentException>(() => _store.UseVersionAsync(User, name, 1));
        await Should.ThrowAsync<ArgumentException>(() => _store.UndoAsync(User, name, DateTimeOffset.UtcNow));
        (await _store.ReadDraftFilesAsync(User, Session, name)).ShouldBeNull();
        (await _store.GetValueAsync(User, name, "k")).ShouldBeNull();
        (await _store.KeysAsync(User, name)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_name_of_65_characters_is_refused()
    {
        var name = new string('a', 65);
        await Should.ThrowAsync<ArgumentException>(() => _store.KeepAsync(User, Session, name, NoSource, NoCheck));
        (await _store.GetAsync(User, name)).Versions.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task A_bad_session_id_throws_on_writes_and_reads_as_nothing(string session)
    {
        Should.Throw<ArgumentException>(() => _store.DraftFolder(User, session, Name));
        await Should.ThrowAsync<ArgumentException>(() => _store.KeepAsync(User, session, Name, NoSource, NoCheck));
        (await _store.ListDraftsAsync(User, session)).ShouldBeEmpty();
        (await _store.GetDraftAsync(User, session, Name)).ShouldBeNull();
    }

    [Fact]
    public async Task List_leaves_out_store_only_and_draft_folders()
    {
        Draft(name: "zeta-mod", manifest: Manifest.Replace("test-chips", "zeta-mod"));
        Draft(name: "alpha-mod", manifest: Manifest.Replace("test-chips", "alpha-mod"));
        await _store.KeepAsync(User, Session, "zeta-mod", NoSource, NoCheck);
        await _store.KeepAsync(User, Session, "alpha-mod", NoSource, NoCheck);
        await _store.SetValueAsync(User, "only-store", "k", Json("1"));
        Draft(name: Name);

        (await _store.ListAsync(User)).Select(h => h.Name).ShouldBe(["alpha-mod", "zeta-mod"]);
    }

    [Fact]
    public async Task The_active_version_can_change_and_must_exist()
    {
        Draft();
        await _store.KeepAsync(User, Session, Name, NoSource, NoCheck);
        Draft();
        await _store.KeepAsync(User, Session, Name, NoSource, NoCheck);

        await _store.UseVersionAsync(User, Name, 1);
        (await _store.GetAsync(User, Name)).ActiveVersion!.Number.ShouldBe(1);

        await Should.ThrowAsync<ModStoreException>(() => _store.UseVersionAsync(User, Name, 9));
        (await _store.GetAsync(User, Name)).Active.ShouldBe(1);
    }

    [Fact]
    public async Task A_mod_can_be_turned_off_and_on()
    {
        Draft();
        await _store.KeepAsync(User, Session, Name, NoSource, NoCheck);
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
        await _store.KeepAsync(User, Session, Name, NoSource, NoCheck);
        var versionFiles = await _store.ReadVersionFilesAsync(User, Name, 1);
        versionFiles!.Select(f => f.Path).ShouldBe(["mod.json", "mod.ts", "pages/deep/a.html"]);
        (await _store.ReadVersionFilesAsync(User, Name, 2)).ShouldBeNull();
        (await _store.ReadDraftFilesAsync(User, Session, Name)).ShouldBeNull();
    }

    [Fact]
    public async Task A_drafts_manifest_is_read_or_null()
    {
        var folder = Draft();
        (await _store.GetDraftAsync(User, Session, Name))!.Manifest.ShouldBe(new ModManifest(Name, "0.1.0", "Shows chips.", "mod.ts"));
        (await _store.ListDraftsAsync(User, Session)).Single().Manifest.ShouldBe(new ModManifest(Name, "0.1.0", "Shows chips.", "mod.ts"));

        File.WriteAllText(Path.Combine(folder, "mod.json"), "nope");
        (await _store.GetDraftAsync(User, Session, Name))!.Manifest.ShouldBeNull();
        File.WriteAllText(Path.Combine(folder, "mod.json"), """{"name":"x","version":"1","description":"","hooks":"a.ts"}""");
        (await _store.GetDraftAsync(User, Session, Name))!.Manifest.ShouldBeNull();
        File.WriteAllText(Path.Combine(folder, "mod.json"), """{"name":"x","version":"1","description":"d","hooks":5}""");
        (await _store.GetDraftAsync(User, Session, Name))!.Manifest.ShouldBeNull();
        File.Delete(Path.Combine(folder, "mod.json"));
        var draft = (await _store.GetDraftAsync(User, Session, Name))!;
        draft.Manifest.ShouldBeNull();
        draft.Folder.ShouldBe(folder);
    }

    [Fact]
    public async Task A_manifest_over_64_KiB_reads_as_null()
    {
        var folder = Draft();
        File.WriteAllText(Path.Combine(folder, "mod.json"), Manifest.Replace("Shows chips.", new string('x', 70 * 1024)));

        (await _store.GetDraftAsync(User, Session, Name))!.Manifest.ShouldBeNull();
    }

    [Fact]
    public async Task A_kept_versions_manifest_is_read_only_when_the_index_lists_it()
    {
        Draft();
        await Keep();

        (await _store.ReadVersionManifestAsync(User, Name, 1)).ShouldBe(new ModManifest(Name, "0.1.0", "Shows chips.", "mod.ts"));
        (await _store.ReadVersionManifestAsync(User, Name, 2)).ShouldBeNull();

        var orphan = Path.Combine(UserFolder(), Name, "v7");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "mod.json"), Manifest);
        (await _store.ReadVersionManifestAsync(User, Name, 7)).ShouldBeNull();
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
    public async Task A_key_must_be_1_to_64_letters_digits_underscore_dash_or_dot()
    {
        await Should.ThrowAsync<ArgumentException>(() => _store.SetValueAsync(User, Name, "", Json("1")));
        await Should.ThrowAsync<ArgumentException>(() => _store.SetValueAsync(User, Name, new string('k', 65), Json("1")));
        await _store.SetValueAsync(User, Name, new string('k', 64), Json("1"));
        await _store.SetValueAsync(User, Name, "a.b_c-d", Json("1"));
        await _store.SetValueAsync(User, Name, ".", Json("1"));
        await _store.SetValueAsync(User, Name, "_", Json("1"));
        await _store.SetValueAsync(User, Name, "-", Json("1"));

        foreach (var key in new[] { "a/b", "a b", "a\\b", "caf\u00e9", "\u65e5\u672c", "a:b", "a\n" })
            await Should.ThrowAsync<ArgumentException>(() => _store.SetValueAsync(User, Name, key, Json("1")));

        (await _store.GetValueAsync(User, Name, "a/b")).ShouldBeNull();
    }

    [Fact]
    public async Task The_store_size_counts_utf8_json_without_escapes()
    {
        // The default encoder would write each \u00e9 as 6 characters: 12 KB for this value.
        await _store.SetValueAsync(User, Name, "k", Json($"\"{new string('\u00e9', 2000)}\""));

        var length = new FileInfo(Path.Combine(UserFolder(), Name, "store.json")).Length;
        length.ShouldBe(2 * 2000 + 8);
        File.ReadAllText(Path.Combine(UserFolder(), Name, "store.json")).ShouldNotContain("\\u");
    }

    [Fact]
    public async Task The_limit_holds_exactly_at_4_MiB_of_utf8()
    {
        // {"k":"<é x N>"} is 8 bytes around the string, and each é is 2 bytes.
        var count = (ModStoreLimits.StoreBytes - 8) / 2;
        await _store.SetValueAsync(User, Name, "k", Json($"\"{new string('\u00e9', count)}\""));
        new FileInfo(Path.Combine(UserFolder(), Name, "store.json")).Length.ShouldBe(ModStoreLimits.StoreBytes);

        await Should.ThrowAsync<ModStoreFullException>(() => _store.SetValueAsync(User, Name, "k", Json($"\"{new string('\u00e9', count + 1)}\"")));
    }

    [Fact]
    public async Task A_draft_and_the_kept_mod_share_one_store()
    {
        Draft();
        await _store.SetValueAsync(User, Name, "seen", Json("3"));

        await _store.KeepAsync(User, Session, Name, NoSource, NoCheck);

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
            .Select(i => Task.Run(() => _store.KeepAsync(User, $"ses_{i}", Name, NoSource, NoCheck))));

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

    // ── Only regular files ──────────────────────────────────────────────

    private static void MakeFifo(string path)
    {
        using var process = Process.Start("mkfifo", path)!;
        process.WaitForExit();
        process.ExitCode.ShouldBe(0);
    }

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Trait("Category", "ModsFileSafety")]
    [Fact]
    public async Task Keep_refuses_a_draft_holding_a_named_pipe_without_blocking()
    {
        if (OperatingSystem.IsWindows())
            return;
        var folder = Draft();
        MakeFifo(Path.Combine(folder, "pipe"));

        var error = await Should.ThrowAsync<ModStoreException>(() => Keep().WaitAsync(Patience));

        error.Message.ShouldContain("regular", Case.Insensitive);
        (await _store.GetAsync(User, Name)).Versions.ShouldBeEmpty();
        Directory.GetDirectories(Path.Combine(UserFolder(), Name)).ShouldBeEmpty();
        Directory.Exists(folder).ShouldBeTrue();
        // Another user's call still goes through.
        (await _store.KeysAsync("someone-else", Name).WaitAsync(Patience)).ShouldBeEmpty();
    }

    [Trait("Category", "ModsFileSafety")]
    [Fact]
    public async Task Show_code_skips_a_named_pipe_without_blocking()
    {
        if (OperatingSystem.IsWindows())
            return;
        var folder = Draft();
        MakeFifo(Path.Combine(folder, "pipe"));
        MakeFifo(Path.Combine(folder, "mod.json.fifo"));

        var files = await _store.ReadDraftFilesAsync(User, Session, Name).WaitAsync(Patience);

        files!.Select(f => f.Path).ShouldBe(["mod.json", "mod.ts"]);
    }

    [Trait("Category", "ModsFileSafety")]
    [Fact]
    public async Task A_named_pipe_as_the_manifest_reads_as_no_manifest_without_blocking()
    {
        if (OperatingSystem.IsWindows())
            return;
        var folder = Draft(manifest: null);
        MakeFifo(Path.Combine(folder, "mod.json"));

        (await _store.GetDraftAsync(User, Session, Name).WaitAsync(Patience))!.Manifest.ShouldBeNull();
        await Should.ThrowAsync<ModStoreException>(() => Keep().WaitAsync(Patience));
    }

    // ── Locks per user and mod ──────────────────────────────────────────

    [Fact]
    public async Task A_slow_keep_blocks_only_its_own_mod()
    {
        Draft();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keeping = Keep(check: async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return null;
        });
        await entered.Task.WaitAsync(Patience);

        (await _store.KeysAsync("someone-else", Name).WaitAsync(Patience)).ShouldBeEmpty();
        (await _store.GetAsync(User, "other-mod").WaitAsync(Patience)).Versions.ShouldBeEmpty();
        await _store.SetValueAsync(User, "other-mod", "k", Json("1")).WaitAsync(Patience);
        await _store.SetDraftOffAsync(User, "ses_other", "other-mod", null).WaitAsync(Patience);
        (await _store.ListDraftsAsync(User, "ses_other").WaitAsync(Patience)).ShouldBeEmpty();

        // The same mod isn't held either: the check runs outside the locks.
        (await _store.GetAsync(User, Name).WaitAsync(Patience)).Versions.ShouldBeEmpty();

        release.SetResult();
        (await keeping.WaitAsync(Patience)).Number.ShouldBe(1);
        (await _store.GetAsync(User, Name).WaitAsync(Patience)).Versions.Count.ShouldBe(1);
    }

    // ── No links from the user's folder down ────────────────────────────

    /// <summary>The user's folder, created if it isn't there yet (by a harmless write to another mod).</summary>
    private string UserFolder0()
    {
        _store.SetValueAsync(User, "zz-anchor", "k", Json("1")).GetAwaiter().GetResult();
        return UserFolder();
    }

    [Trait("Category", "ModsFileSafety")]
    [Fact]
    public async Task Show_code_and_Keep_do_not_follow_a_linked_draft_folder()
    {
        var outside = Outside();
        var session = Path.Combine(UserFolder0(), "drafts", Session);
        Directory.CreateDirectory(session);
        Directory.CreateSymbolicLink(Path.Combine(session, Name), outside);

        (await _store.ReadDraftFilesAsync(User, Session, Name)).ShouldBeNull();
        (await _store.GetDraftAsync(User, Session, Name)).ShouldBeNull();
        (await _store.ListDraftsAsync(User, Session)).ShouldBeEmpty();
        var error = await Should.ThrowAsync<ModStoreException>(() => Keep());

        error.Message.ShouldContain("link", Case.Insensitive);
        File.ReadAllText(Path.Combine(outside, "secret.txt")).ShouldBe("outside");
        File.Exists(Path.Combine(outside, "mod.ts")).ShouldBeTrue();
        (await _store.GetAsync(User, Name)).Versions.ShouldBeEmpty();
    }

    [Trait("Category", "ModsFileSafety")]
    [Fact]
    public async Task Keep_refuses_a_linked_session_folder_and_leaves_what_it_points_at_alone()
    {
        var outside = Outside();
        var inner = Path.Combine(outside, Name);
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "mod.json"), Manifest);
        File.WriteAllText(Path.Combine(inner, "mod.ts"), "export default {};");
        var drafts = Path.Combine(UserFolder0(), "drafts");
        Directory.CreateDirectory(drafts);
        Directory.CreateSymbolicLink(Path.Combine(drafts, Session), outside);

        (await _store.ReadDraftFilesAsync(User, Session, Name)).ShouldBeNull();
        (await _store.ListDraftsAsync(User, Session)).ShouldBeEmpty();
        var error = await Should.ThrowAsync<ModStoreException>(() => Keep());

        error.Message.ShouldContain("link", Case.Insensitive);
        Directory.Exists(outside).ShouldBeTrue();
        File.Exists(Path.Combine(outside, "secret.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(inner, "mod.ts")).ShouldBeTrue();
        await Should.ThrowAsync<ModStoreException>(() => _store.SetDraftOffAsync(User, Session, Name, new ModOff(ModOffBy.User, DateTimeOffset.UtcNow)));
        (await _store.GetAsync(User, Name)).Versions.ShouldBeEmpty();
    }

    [Trait("Category", "ModsFileSafety")]
    [Fact]
    public async Task Keep_refuses_a_linked_drafts_folder()
    {
        var outside = Outside();
        var inner = Path.Combine(outside, Session, Name);
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "mod.json"), Manifest);
        File.WriteAllText(Path.Combine(inner, "mod.ts"), "export default {};");
        Directory.CreateSymbolicLink(Path.Combine(UserFolder0(), "drafts"), outside);

        await Should.ThrowAsync<ModStoreException>(() => Keep());
        (await _store.ListDraftsAsync(User, Session)).ShouldBeEmpty();
        File.Exists(Path.Combine(inner, "mod.ts")).ShouldBeTrue();
    }

    [Trait("Category", "ModsFileSafety")]
    [Fact]
    public async Task Keep_refuses_a_linked_folder_inside_the_draft()
    {
        var outside = Outside();
        var folder = Draft();
        Directory.CreateSymbolicLink(Path.Combine(folder, "pages"), outside);

        var error = await Should.ThrowAsync<ModStoreException>(() => Keep());

        error.Message.ShouldContain("link", Case.Insensitive);
        File.Exists(Path.Combine(outside, "secret.txt")).ShouldBeTrue();
        (await _store.ReadDraftFilesAsync(User, Session, Name))!.Select(f => f.Path).ShouldBe(["mod.json", "mod.ts"]);
    }

    [Trait("Category", "ModsFileSafety")]
    [Fact]
    public async Task A_linked_mod_folder_is_never_followed()
    {
        var outside = Outside();
        var kept = Path.Combine(outside, "v1");
        Directory.CreateDirectory(kept);
        File.WriteAllText(Path.Combine(kept, "mod.json"), Manifest);
        File.WriteAllText(Path.Combine(kept, "mod.ts"), "export default {};");
        File.WriteAllText(Path.Combine(outside, "store.json"), """{"k":1}""");
        File.WriteAllText(Path.Combine(outside, "versions.json"),
            """{"name":"test-chips","active":1,"off":null,"versions":[{"number":1,"createdAt":"2026-10-10T00:00:00+00:00","version":"0.1.0","sha256":"ab","sessionId":null,"sessionTitle":null,"note":null,"check":null}]}""");
        Directory.CreateSymbolicLink(Path.Combine(UserFolder0(), Name), outside);

        (await _store.GetAsync(User, Name)).ShouldBe(ModHistory.Empty(Name));
        (await _store.ListAsync(User)).ShouldBeEmpty();
        (await _store.ReadVersionFilesAsync(User, Name, 1)).ShouldBeNull();
        (await _store.ReadVersionManifestAsync(User, Name, 1)).ShouldBeNull();
        (await _store.GetValueAsync(User, Name, "k")).ShouldBeNull();
        (await _store.KeysAsync(User, Name)).ShouldBeEmpty();
        await Should.ThrowAsync<ModStoreException>(() => _store.SetValueAsync(User, Name, "j", Json("2")));
        await Should.ThrowAsync<ModStoreException>(() => _store.UseVersionAsync(User, Name, 1));
        await Should.ThrowAsync<ModStoreException>(() => _store.SetOffAsync(User, Name, new ModOff(ModOffBy.User, DateTimeOffset.UtcNow)));
        Draft();
        await Should.ThrowAsync<ModStoreException>(() => Keep());

        Directory.GetFileSystemEntries(outside).Select(Path.GetFileName).Order().ShouldBe(["mod.json", "mod.ts", "secret.txt", "store.json", "v1", "versions.json"]);
        File.ReadAllText(Path.Combine(outside, "store.json")).ShouldBe("""{"k":1}""");
    }

    [Trait("Category", "ModsFileSafety")]
    [Fact]
    public async Task A_linked_store_or_off_file_is_never_followed()
    {
        var outside = Outside();
        Draft();
        await _store.SetValueAsync(User, Name, "k", Json("1"));
        var mod = Path.Combine(UserFolder(), Name);
        File.Move(Path.Combine(mod, "store.json"), Path.Combine(outside, "store.json"));
        File.CreateSymbolicLink(Path.Combine(mod, "store.json"), Path.Combine(outside, "store.json"));
        File.WriteAllText(Path.Combine(outside, "off.json"), """{"test-chips":{"by":"user","at":"2026-10-10T00:00:00+00:00"}}""");
        File.CreateSymbolicLink(Path.Combine(UserFolder(), "drafts", Session, "off.json"), Path.Combine(outside, "off.json"));

        (await _store.GetValueAsync(User, Name, "k")).ShouldBeNull();
        await Should.ThrowAsync<ModStoreException>(() => _store.SetValueAsync(User, Name, "j", Json("2")));
        (await _store.GetDraftAsync(User, Session, Name))!.Off.ShouldBeNull();
        File.ReadAllText(Path.Combine(outside, "store.json")).ShouldBe("""{"k":1}""");
    }

    // ── Keep copies first, then checks the copy ─────────────────────────

    [Fact]
    public async Task Keep_checks_a_copy_and_keeps_that_copy()
    {
        var folder = Draft(code: "// copied\n");
        string? staged = null;
        var sawCopy = false;

        var version = await Keep(check: (stagedFolder, _) =>
        {
            staged = stagedFolder;
            sawCopy = stagedFolder != folder && File.ReadAllText(Path.Combine(stagedFolder, "mod.ts")) == "// copied\n";
            return Task.FromResult<JsonElement?>(null);
        });

        sawCopy.ShouldBeTrue();
        Path.GetFileName(staged!).ShouldEndWith(".tmp");
        Directory.Exists(staged!).ShouldBeFalse();
        File.ReadAllText(Path.Combine(_store.VersionFolder(User, Name, 1), "mod.ts")).ShouldBe("// copied\n");
        version.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("// copied\n"))));
        (await _store.GetAsync(User, Name)).Versions.Single().Sha256.ShouldBe(version.Sha256);
    }

    [Fact]
    public async Task Keep_refuses_a_report_that_is_not_ok_and_leaves_no_staging_folder()
    {
        var folder = Draft();

        var error = await Should.ThrowAsync<ModStoreException>(
            () => Keep(check: Report("""{"ok":false,"errors":[{"message":"uses eval"}]}""")));

        error.Message.ShouldContain("uses eval");
        (await _store.GetAsync(User, Name)).Versions.ShouldBeEmpty();
        Directory.GetFileSystemEntries(Path.Combine(UserFolder(), Name)).ShouldBeEmpty();
        Directory.Exists(folder).ShouldBeTrue();
    }

    [Fact]
    public async Task A_check_that_throws_leaves_no_staging_folder()
    {
        Draft();

        await Should.ThrowAsync<InvalidOperationException>(() => Keep(check: (_, _) => throw new InvalidOperationException("boom")));

        Directory.GetFileSystemEntries(Path.Combine(UserFolder(), Name)).ShouldBeEmpty();
        (await Keep()).Number.ShouldBe(1);
    }

    [Fact]
    public async Task The_check_report_is_stored_with_the_version()
    {
        Draft();

        var version = await Keep(check: Report("""{"ok":true,"warnings":[]}"""));

        version.Check!.Value.GetProperty("ok").GetBoolean().ShouldBeTrue();
        (await _store.GetAsync(User, Name)).Versions.Single().Check!.Value.GetProperty("warnings").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Keep_removes_staging_folders_a_crash_left_behind()
    {
        Draft();
        var mod = Path.Combine(UserFolder(), Name);
        Directory.CreateDirectory(Path.Combine(mod, "v3.0123456789abcdef.tmp", "deep"));
        File.WriteAllText(Path.Combine(mod, "v3.0123456789abcdef.tmp", "deep", "x.txt"), "x");
        Directory.CreateDirectory(Path.Combine(mod, "keep.fedcba.tmp"));
        foreach (var leftover in Directory.GetDirectories(mod))
            Directory.SetLastWriteTimeUtc(leftover, DateTime.UtcNow.AddHours(-2));

        var version = await Keep();

        version.Number.ShouldBe(1);
        Directory.GetFileSystemEntries(mod).Select(Path.GetFileName).Order().ShouldBe(["v1", "versions.json"]);
    }

    [Fact]
    public async Task Kept_files_are_read_only()
    {
        var folder = Draft();
        Directory.CreateDirectory(Path.Combine(folder, "pages"));
        File.WriteAllText(Path.Combine(folder, "pages", "a.html"), "<p>hi</p>");
        await Keep();

        var version = _store.VersionFolder(User, Name, 1);
        foreach (var file in Directory.EnumerateFiles(version, "*", SearchOption.AllDirectories))
        {
            File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly).ShouldBeTrue(file);
            if (!OperatingSystem.IsWindows())
                File.GetUnixFileMode(file).ShouldBe(UnixFileMode.UserRead, file);
        }

        // Directories stay traversable.
        (await _store.ReadVersionFilesAsync(User, Name, 1))!.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_draft_that_cannot_be_deleted_does_not_fail_Keep()
    {
        if (OperatingSystem.IsWindows())
            return;
        Draft();
        var session = Path.GetDirectoryName(_store.DraftFolder(User, Session, Name))!;
        File.SetUnixFileMode(session, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            try
            {
                File.WriteAllText(Path.Combine(session, "probe"), "x");
                return; // Running as a user who can write anyway (root): nothing to test.
            }
            catch (UnauthorizedAccessException)
            {
            }

            var version = await Keep();

            version.Number.ShouldBe(1);
            (await _store.GetAsync(User, Name)).Versions.Single().Number.ShouldBe(1);
            Directory.Exists(_store.VersionFolder(User, Name, 1)).ShouldBeTrue();
        }
        finally
        {
            File.SetUnixFileMode(session, (UnixFileMode)0b111_111_111);
        }
    }

    [Fact]
    public async Task Keep_counts_files_while_copying()
    {
        var folder = Draft();
        for (var i = 0; i < 497; i++)
            File.WriteAllText(Path.Combine(folder, $"f{i}.txt"), "x");

        // mod.json, mod.ts and 497 files: 499.
        (await Keep()).Number.ShouldBe(1);

        var again = Draft();
        for (var i = 0; i < 499; i++)
            File.WriteAllText(Path.Combine(again, $"f{i}.txt"), "x");
        await Should.ThrowAsync<ModStoreException>(() => Keep());
        Directory.GetDirectories(Path.Combine(UserFolder(), Name)).Select(Path.GetFileName).ShouldBe(["v1"]);
    }

    // ── Use version, Undo and Off ───────────────────────────────────────

    /// <summary>The history on disk is the one the call answered with (a record compares its version list by reference).</summary>
    private async Task ShouldBeStored(ModHistory answered)
    {
        var stored = await _store.GetAsync(User, Name);
        stored.Active.ShouldBe(answered.Active);
        stored.Off.ShouldBe(answered.Off);
        stored.Versions.Select(v => v.Number).ShouldBe(answered.Versions.Select(v => v.Number));
    }

    private async Task KeepVersions(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            Draft(code: $"// {i}\n");
            await Keep();
        }
    }

    [Fact]
    public async Task Use_version_makes_it_active_and_turns_the_mod_on()
    {
        await KeepVersions(3);
        var at = new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero);
        await _store.SetOffAsync(User, Name, new ModOff(ModOffBy.Strikes, at, "boom"));

        var history = await _store.UseVersionAsync(User, Name, 2);

        history.Active.ShouldBe(2);
        history.Off.ShouldBeNull();
        await ShouldBeStored(history);
        (await _store.GetAsync(User, Name)).Versions.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Use_version_of_a_missing_version_or_mod_throws_and_changes_nothing()
    {
        await KeepVersions(1);
        var off = new ModOff(ModOffBy.User, DateTimeOffset.UtcNow);
        await _store.SetOffAsync(User, Name, off);

        await Should.ThrowAsync<ModStoreException>(() => _store.UseVersionAsync(User, Name, 9));
        await Should.ThrowAsync<ModStoreException>(() => _store.UseVersionAsync(User, "other-mod", 1));

        (await _store.GetAsync(User, Name)).Off.ShouldBe(off);
    }

    [Fact]
    public async Task Undo_goes_back_one_version_and_turns_the_mod_on()
    {
        await KeepVersions(3);
        var at = new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero);

        var history = await _store.UndoAsync(User, Name, at);

        history.Active.ShouldBe(2);
        history.Off.ShouldBeNull();
        await ShouldBeStored(history);
        (await _store.UndoAsync(User, Name, at)).Active.ShouldBe(1);
    }

    [Fact]
    public async Task Undo_on_an_off_mod_at_a_later_version_makes_the_previous_one_active_and_on()
    {
        await KeepVersions(3);
        await _store.SetOffAsync(User, Name, new ModOff(ModOffBy.Strikes, DateTimeOffset.UtcNow, "boom"));

        var history = await _store.UndoAsync(User, Name, DateTimeOffset.UtcNow);

        history.Active.ShouldBe(2);
        history.Off.ShouldBeNull();
    }

    [Fact]
    public async Task Undo_means_the_highest_listed_version_below_the_active_one()
    {
        await KeepVersions(3);
        var path = Path.Combine(UserFolder(), Name, "versions.json");
        var index = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        index["versions"]!.AsArray().RemoveAt(1);
        File.WriteAllText(path, index.ToJsonString());
        Remove(Path.Combine(UserFolder(), Name, "v2"));

        (await _store.GetAsync(User, Name)).Versions.Select(v => v.Number).ShouldBe([1, 3]);
        (await _store.UndoAsync(User, Name, DateTimeOffset.UtcNow)).Active.ShouldBe(1);
    }

    [Fact]
    public async Task Undo_on_the_first_version_turns_the_mod_off_and_keeps_it_active()
    {
        await KeepVersions(1);
        var at = new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero);

        var history = await _store.UndoAsync(User, Name, at);

        history.Active.ShouldBe(1);
        history.Off.ShouldBe(new ModOff(ModOffBy.User, at));
        await ShouldBeStored(history);
        Directory.Exists(_store.VersionFolder(User, Name, 1)).ShouldBeTrue();
    }

    [Fact]
    public async Task Undo_on_an_off_first_version_has_nothing_to_undo()
    {
        await KeepVersions(1);
        await _store.UndoAsync(User, Name, DateTimeOffset.UtcNow);

        var error = await Should.ThrowAsync<ModStoreException>(() => _store.UndoAsync(User, Name, DateTimeOffset.UtcNow));

        error.Message.ShouldBe("Nothing to undo");
        (await _store.GetAsync(User, Name)).Off.ShouldNotBeNull();
    }

    [Fact]
    public async Task Undo_of_a_mod_that_was_never_kept_throws()
        => await Should.ThrowAsync<ModStoreException>(() => _store.UndoAsync(User, Name, DateTimeOffset.UtcNow));

    [Fact]
    public async Task Set_off_answers_with_the_history()
    {
        await KeepVersions(2);
        var off = new ModOff(ModOffBy.User, DateTimeOffset.UtcNow);

        var history = await _store.SetOffAsync(User, Name, off);

        history.Off.ShouldBe(off);
        history.Active.ShouldBe(2);
        history.Versions.Count.ShouldBe(2);
        (await _store.SetOffAsync(User, Name, null)).Off.ShouldBeNull();
    }

    [Fact]
    public async Task Undo_and_Keep_in_parallel_leave_a_consistent_index()
    {
        await KeepVersions(2);
        Draft(session: "ses_x", code: "// three\n");

        var undo = _store.UndoAsync(User, Name, DateTimeOffset.UtcNow);
        var keep = Keep(session: "ses_x");
        await Task.WhenAll(undo, keep);

        var history = await _store.GetAsync(User, Name);
        history.Versions.Select(v => v.Number).ShouldBe([1, 2, 3]);
        // Either order is fine, but the writes were serialised: no version was lost.
        (history.Active is 1 or 3).ShouldBeTrue();
    }

    // ── A broken index never loses history ──────────────────────────────

    [Theory]
    [InlineData("{ nope")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("")]
    public async Task A_broken_index_is_moved_aside_and_rebuilt_from_the_version_folders(string broken)
    {
        await KeepVersions(2);
        var index = Path.Combine(UserFolder(), Name, "versions.json");
        File.WriteAllText(index, broken);

        var history = await _store.GetAsync(User, Name);

        history.Versions.Select(v => v.Number).ShouldBe([1, 2]);
        history.Active.ShouldBe(2);
        history.Off.ShouldBeNull();
        var first = history.Versions[0];
        first.Version.ShouldBe("0.1.0");
        first.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("// 1\n"))));
        first.SessionId.ShouldBeNull();
        first.SessionTitle.ShouldBeNull();
        first.Note.ShouldBeNull();
        first.Check.ShouldBeNull();
        (DateTimeOffset.UtcNow - first.CreatedAt).ShouldBeLessThan(TimeSpan.FromMinutes(5));
        var aside = Directory.GetFiles(Path.GetDirectoryName(index)!, "versions.json.broken-*").ShouldHaveSingleItem();
        File.ReadAllText(aside).ShouldBe(broken);
        File.Exists(index).ShouldBeTrue();
    }

    [Fact]
    public async Task Keep_after_a_broken_index_lists_every_version()
    {
        await KeepVersions(2);
        var index = Path.Combine(UserFolder(), Name, "versions.json");
        File.WriteAllText(index, "{ nope");

        Draft(code: "// 3\n");
        var version = await Keep();

        version.Number.ShouldBe(3);
        var history = await _store.GetAsync(User, Name);
        history.Versions.Select(v => v.Number).ShouldBe([1, 2, 3]);
        history.Active.ShouldBe(3);
        Directory.GetFiles(Path.GetDirectoryName(index)!, "versions.json.broken-*").ShouldHaveSingleItem();
    }

    // ── Show code ───────────────────────────────────────────────────────

    [Fact]
    public async Task Show_code_serves_only_versions_the_index_lists()
    {
        await KeepVersions(1);
        var orphan = Path.Combine(UserFolder(), Name, "v7");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "notes.txt"), "not a mod");

        (await _store.ReadVersionFilesAsync(User, Name, 7)).ShouldBeNull();
        (await _store.ReadVersionFilesAsync(User, Name, 1))!.Count.ShouldBe(2);
        // The orphan still counts for the next number.
        Draft();
        (await Keep()).Number.ShouldBe(8);
    }

    [Fact]
    public async Task Show_code_stops_at_the_file_cap()
    {
        var folder = Draft();
        for (var i = 0; i < 250; i++)
            File.WriteAllText(Path.Combine(folder, $"f{i:000}.txt"), "x");

        var files = await _store.ReadDraftFilesAsync(User, Session, Name);

        files!.Count.ShouldBe(ModStoreLimits.ShownFiles);
    }

    [Fact]
    public async Task Show_code_of_a_kept_version_stops_at_the_file_cap()
    {
        var folder = Draft();
        for (var i = 0; i < 300; i++)
            File.WriteAllText(Path.Combine(folder, $"f{i:000}.txt"), "x");
        await Keep();

        (await _store.ReadVersionFilesAsync(User, Name, 1))!.Count.ShouldBe(ModStoreLimits.ShownFiles);
    }

    [Fact]
    public async Task Show_code_stops_at_the_byte_cap()
    {
        var folder = Draft();
        for (var i = 0; i < 12; i++)
            File.WriteAllText(Path.Combine(folder, $"big{i:00}.txt"), new string('x', 450_000));

        var files = (await _store.ReadDraftFilesAsync(User, Session, Name))!;

        files.Sum(f => (long)Encoding.UTF8.GetByteCount(f.Content)).ShouldBeLessThanOrEqualTo(ModStoreLimits.ShownBytes);
        files.Count.ShouldBeLessThan(14);
        files.Count.ShouldBeGreaterThan(5);
    }

    // ── Honest messages ─────────────────────────────────────────────────

    [Fact]
    public async Task Keep_says_a_file_it_cannot_read_cannot_be_read_and_names_it()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
            return;
        var folder = Draft();
        Directory.CreateDirectory(Path.Combine(folder, "lib"));
        var secret = Path.Combine(folder, "lib", "locked.ts");
        File.WriteAllText(secret, "x");
        File.SetUnixFileMode(secret, 0);

        var error = await Should.ThrowAsync<ModStoreException>(() => Keep().WaitAsync(Patience));

        error.Message.ShouldContain("lib/locked.ts");
        error.Message.ShouldContain("can't be read", Case.Insensitive);
        error.Message.ShouldNotContain("regular", Case.Insensitive);
    }

    [Fact]
    public async Task Show_code_skips_a_file_it_cannot_read_and_keeps_going()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
            return;
        var folder = Draft();
        File.WriteAllText(Path.Combine(folder, "a-locked.ts"), "x");
        File.WriteAllText(Path.Combine(folder, "z-after.ts"), "after");
        File.SetUnixFileMode(Path.Combine(folder, "a-locked.ts"), 0);

        var files = await _store.ReadDraftFilesAsync(User, Session, Name).WaitAsync(Patience);

        files!.Select(f => f.Path).ShouldBe(["mod.json", "mod.ts", "z-after.ts"]);
    }

    [Fact]
    public async Task Keep_says_a_huge_file_is_too_large_without_reading_it()
    {
        var folder = Draft();
        using (var big = new FileStream(Path.Combine(folder, "big.bin"), FileMode.Create))
            big.SetLength(5L * 1024 * 1024 * 1024);

        var watch = Stopwatch.StartNew();
        var error = await Should.ThrowAsync<ModStoreException>(() => Keep().WaitAsync(Patience));
        var files = await _store.ReadDraftFilesAsync(User, Session, Name).WaitAsync(Patience);

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        error.Message.ShouldContain("big.bin");
        error.Message.ShouldContain("too large", Case.Insensitive);
        error.Message.ShouldNotContain("regular", Case.Insensitive);
        files!.Select(f => f.Path).ShouldBe(["mod.json", "mod.ts"]);
    }

    [Trait("Category", "ModsFileSafety")]
    [Fact]
    public async Task Keep_says_a_named_pipe_is_not_a_regular_file_and_names_it()
    {
        if (OperatingSystem.IsWindows())
            return;
        var folder = Draft();
        MakeFifo(Path.Combine(folder, "pipe"));

        var error = await Should.ThrowAsync<ModStoreException>(() => Keep().WaitAsync(Patience));

        error.Message.ShouldContain("pipe");
        error.Message.ShouldContain("isn't a regular file");
    }

    [Fact]
    public async Task Keep_refuses_a_directory_where_the_hooks_file_should_be()
    {
        var folder = Draft(manifest: """{"name":"test-chips","version":"0.1.0","description":"d","hooks":"hooks.ts"}""");
        Directory.CreateDirectory(Path.Combine(folder, "hooks.ts"));

        await Should.ThrowAsync<ModStoreException>(() => Keep().WaitAsync(Patience));
    }

    [Trait("Category", "ModsFileSafety")]
    [Fact]
    public async Task A_name_swapped_between_a_file_and_a_named_pipe_never_hangs_Show_code_or_Keep()
    {
        if (OperatingSystem.IsWindows())
            return;
        var folder = Draft();
        var target = Path.Combine(folder, "a.ts");
        var regular = Path.Combine(folder, ".r");
        var pipe = Path.Combine(folder, ".f");
        File.WriteAllText(regular, "x");
        MakeFifo(pipe);
        using var stop = new CancellationTokenSource();
        var swapper = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    File.Move(regular, target, overwrite: true);
                    File.Move(target, regular, overwrite: true);
                    File.Move(pipe, target, overwrite: true);
                    File.Move(target, pipe, overwrite: true);
                }
                catch (IOException)
                {
                }
            }
        });

        try
        {
            for (var i = 0; i < 300; i++)
            {
                (await _store.ReadDraftFilesAsync(User, Session, Name).WaitAsync(Patience)).ShouldNotBeNull();
                try
                {
                    await Keep(check: (_, _) => throw new ModStoreException("stop here")).WaitAsync(Patience);
                }
                catch (ModStoreException)
                {
                }
            }
        }
        finally
        {
            stop.Cancel();
            await swapper.WaitAsync(Patience);
        }
    }

    // ── A broken index with valid JSON ──────────────────────────────────

    [Theory]
    [InlineData("""{"versions":[]}""")]
    [InlineData("""{"name":"test-chips","active":2,"versions":[{"number":1,"createdAt":"2026-01-01T00:00:00Z","version":"0.1.0","sha256":"aa"},{"number":2,"createdAt":"2026-01-01T00:00:00Z","version":"0.1.0"}]}""")]
    [InlineData("""{"name":"test-chips","active":9,"versions":[{"number":1,"createdAt":"2026-01-01T00:00:00Z","version":"0.1.0","sha256":"aa"},{"number":2,"createdAt":"2026-01-01T00:00:00Z","version":"0.1.0","sha256":"bb"}]}""")]
    public async Task An_index_that_parses_but_does_not_match_the_folders_is_moved_aside_and_rebuilt(string broken)
    {
        await KeepVersions(2);
        var mod = Path.Combine(UserFolder(), Name);
        File.WriteAllText(Path.Combine(mod, "versions.json"), broken);

        Draft(code: "// 3\n");
        var version = await Keep();

        version.Number.ShouldBe(3);
        var history = await _store.GetAsync(User, Name);
        history.Versions.Select(v => v.Number).ShouldBe([1, 2, 3]);
        history.Active.ShouldBe(3);
        File.ReadAllText(Directory.GetFiles(mod, "versions.json.broken-*").ShouldHaveSingleItem()).ShouldBe(broken);
    }

    [Fact]
    public async Task A_rebuild_keeps_the_valid_entries_as_they_were_and_the_off_state()
    {
        await KeepVersions(2);
        var mod = Path.Combine(UserFolder(), Name);
        var index = Path.Combine(mod, "versions.json");
        File.WriteAllText(index, """
            {"name":"test-chips","active":1,"off":{"by":"user","at":"2026-02-02T00:00:00Z"},
             "versions":[{"number":1,"createdAt":"2026-01-01T00:00:00Z","version":"0.1.0","sha256":"aa","sessionId":"ses_x","note":"first"}]}
            """);

        var history = await _store.GetAsync(User, Name);

        history.Versions.Select(v => v.Number).ShouldBe([1, 2]);
        history.Versions[0].Note.ShouldBe("first");
        history.Versions[0].SessionId.ShouldBe("ses_x");
        history.Versions[0].Sha256.ShouldBe("aa");
        history.Versions[0].CreatedAt.ShouldBe(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        history.Versions[1].SessionId.ShouldBeNull();
        history.Active.ShouldBe(1);
        history.Off.ShouldNotBeNull().By.ShouldBe(ModOffBy.User);
        Directory.GetFiles(mod, "versions.json.broken-*").ShouldHaveSingleItem();
        // Read again: the rebuilt index is consistent, so nothing more is moved aside.
        await _store.GetAsync(User, Name);
        Directory.GetFiles(mod, "versions.json.broken-*").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_staging_folder_is_not_a_version_and_a_folder_that_cannot_be_rebuilt_does_not_loop()
    {
        await KeepVersions(1);
        var mod = Path.Combine(UserFolder(), Name);
        Directory.CreateDirectory(Path.Combine(mod, "v2.0123456789abcdef.tmp"));
        Directory.CreateDirectory(Path.Combine(mod, "v3"));

        await _store.GetAsync(User, Name);
        await _store.GetAsync(User, Name);

        Directory.GetFiles(mod, "versions.json.broken-*").ShouldBeEmpty();
        (await _store.GetAsync(User, Name)).Versions.Select(v => v.Number).ShouldBe([1]);
    }

    // ── The check runs outside the locks ────────────────────────────────

    [Fact]
    public async Task While_Keep_checks_the_session_and_the_mod_are_free()
    {
        Draft();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keeping = Keep(check: async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return null;
        });
        await entered.Task.WaitAsync(Patience);

        try
        {
            (await _store.ListDraftsAsync(User, Session).WaitAsync(Patience)).Count.ShouldBe(1);
            (await _store.ReadDraftFilesAsync(User, Session, Name).WaitAsync(Patience))!.Count.ShouldBe(2);
            (await _store.KeysAsync(User, Name).WaitAsync(Patience)).ShouldBeEmpty();
        }
        finally
        {
            release.SetResult();
        }

        (await keeping.WaitAsync(Patience)).Number.ShouldBe(1);
    }

    [Fact]
    public async Task A_draft_changed_during_the_check_is_refused_and_leaves_no_copy()
    {
        var folder = Draft();

        var error = await Should.ThrowAsync<ModStoreException>(() => Keep(check: (_, _) =>
        {
            File.WriteAllText(Path.Combine(folder, "mod.ts"), "// changed\n");
            return Task.FromResult<JsonElement?>(null);
        }));

        error.Message.ShouldBe("The draft changed while it was being checked; check it again.");
        (await _store.GetAsync(User, Name)).Versions.ShouldBeEmpty();
        Directory.GetFileSystemEntries(Path.Combine(UserFolder(), Name)).ShouldBeEmpty();
        Directory.Exists(folder).ShouldBeTrue();
        (await Keep()).Number.ShouldBe(1);
    }

    [Fact]
    public async Task A_file_added_or_a_draft_removed_during_the_check_is_refused()
    {
        var folder = Draft();
        await Should.ThrowAsync<ModStoreException>(() => Keep(check: (_, _) =>
        {
            File.WriteAllText(Path.Combine(folder, "extra.txt"), "x");
            return Task.FromResult<JsonElement?>(null);
        }));
        File.Delete(Path.Combine(folder, "extra.txt"));

        var error = await Should.ThrowAsync<ModStoreException>(() => Keep(check: (_, _) =>
        {
            Directory.Delete(folder, recursive: true);
            return Task.FromResult<JsonElement?>(null);
        }));

        error.Message.ShouldContain("changed");
        (await _store.GetAsync(User, Name)).Versions.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_draft_that_does_not_change_is_kept_after_a_slow_check()
    {
        Draft();

        var version = await Keep(check: async (_, _) =>
        {
            await Task.Delay(100, CancellationToken.None);
            return null;
        });

        version.Number.ShouldBe(1);
        Directory.Exists(_store.DraftFolder(User, Session, Name)).ShouldBeFalse();
    }

    [Fact]
    public async Task Another_session_can_keep_the_same_mod_while_a_check_runs_and_the_numbers_follow_the_commits()
    {
        Draft();
        Draft(session: "ses_beta", code: "// beta\n");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Keep(check: async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return null;
        });
        await entered.Task.WaitAsync(Patience);

        (await Keep(session: "ses_beta").WaitAsync(Patience)).Number.ShouldBe(1);
        release.SetResult();

        (await first.WaitAsync(Patience)).Number.ShouldBe(2);
        (await _store.GetAsync(User, Name)).Versions.Select(v => v.Number).ShouldBe([1, 2]);
    }

    [Fact]
    public async Task Keep_leaves_alone_a_staging_folder_younger_than_an_hour()
    {
        Draft();
        var mod = Path.Combine(UserFolder(), Name);
        var inFlight = Path.Combine(mod, "check.0123456789abcdef.tmp");
        Directory.CreateDirectory(inFlight);
        File.WriteAllText(Path.Combine(inFlight, "mod.ts"), "x");

        await Keep();

        File.Exists(Path.Combine(inFlight, "mod.ts")).ShouldBeTrue();
    }

    // ── CheckDraftAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task Check_runs_on_a_copy_of_the_draft_and_deletes_it_afterwards()
    {
        var folder = Draft(code: "// live\n");
        string? staged = null;
        string? seen = null;

        var report = await _store.CheckDraftAsync(User, Session, Name, (copy, _) =>
        {
            staged = copy;
            seen = File.ReadAllText(Path.Combine(copy, "mod.ts"));
            File.WriteAllText(Path.Combine(copy, "mod.ts"), "// the checker scribbled\n");
            return Task.FromResult<JsonElement?>(Json("""{"ok":true}"""));
        });

        report!.Value.GetProperty("ok").GetBoolean().ShouldBeTrue();
        seen.ShouldBe("// live\n");
        staged.ShouldNotBe(folder);
        Directory.Exists(staged!).ShouldBeFalse();
        File.ReadAllText(Path.Combine(folder, "mod.ts")).ShouldBe("// live\n");
        (await _store.GetAsync(User, Name)).Versions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Check_holds_no_lock_while_it_runs()
    {
        Draft();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checking = _store.CheckDraftAsync(User, Session, Name, async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return null;
        });
        await entered.Task.WaitAsync(Patience);

        try
        {
            (await _store.ListDraftsAsync(User, Session).WaitAsync(Patience)).Count.ShouldBe(1);
            (await _store.ReadDraftFilesAsync(User, Session, Name).WaitAsync(Patience))!.Count.ShouldBe(2);
            (await _store.KeysAsync(User, Name).WaitAsync(Patience)).ShouldBeEmpty();
        }
        finally
        {
            release.SetResult();
        }

        await checking.WaitAsync(Patience);
    }

    [Fact]
    public async Task Check_without_a_draft_throws()
    {
        await Should.ThrowAsync<ModStoreException>(() => _store.CheckDraftAsync(User, Session, Name, NoCheck));
    }

    [Fact]
    public async Task Check_deletes_its_copy_when_the_checker_throws()
    {
        Draft();

        await Should.ThrowAsync<InvalidOperationException>(
            () => _store.CheckDraftAsync(User, Session, Name, (_, _) => throw new InvalidOperationException("boom")));

        Directory.GetFileSystemEntries(Path.Combine(UserFolder(), Name)).ShouldBeEmpty();
    }

    [Trait("Category", "ModsFileSafety")]
    [Fact]
    public async Task Check_never_hands_the_checker_a_named_pipe_or_a_link()
    {
        if (OperatingSystem.IsWindows())
            return;
        var folder = Draft();
        MakeFifo(Path.Combine(folder, "pipe"));
        var called = false;
        ModKeepCheck check = (_, _) =>
        {
            called = true;
            return Task.FromResult<JsonElement?>(null);
        };

        var error = await Should.ThrowAsync<ModStoreException>(() => _store.CheckDraftAsync(User, Session, Name, check).WaitAsync(Patience));
        File.Delete(Path.Combine(folder, "pipe"));
        File.CreateSymbolicLink(Path.Combine(folder, "link.ts"), Path.Combine(folder, "mod.ts"));
        var linkError = await Should.ThrowAsync<ModStoreException>(() => _store.CheckDraftAsync(User, Session, Name, check).WaitAsync(Patience));

        called.ShouldBeFalse();
        error.Message.ShouldContain("pipe");
        linkError.Message.ShouldContain("link", Case.Insensitive);
        Directory.GetFileSystemEntries(Path.Combine(UserFolder(), Name)).ShouldBeEmpty();
    }

    // ── Staged copies sit in a folder named after the mod ───────────────

    private static void ShouldBeStagedAs(string? staged, string modFolder)
    {
        Path.GetFileName(staged!).ShouldBe(Name);
        var outer = Path.GetDirectoryName(staged!)!;
        Path.GetFileName(outer).ShouldEndWith(".tmp");
        Path.GetDirectoryName(outer).ShouldBe(modFolder);
    }

    [Fact]
    public async Task Keep_hands_the_check_a_folder_named_after_the_mod_inside_a_tmp_folder()
    {
        Draft();
        string? staged = null;

        await Keep(check: (copy, _) =>
        {
            staged = copy;
            File.Exists(Path.Combine(copy, "mod.json")).ShouldBeTrue();
            return Task.FromResult<JsonElement?>(null);
        });

        ShouldBeStagedAs(staged, Path.Combine(UserFolder(), Name));
        File.Exists(Path.Combine(_store.VersionFolder(User, Name, 1), "mod.json")).ShouldBeTrue();
        Directory.GetFileSystemEntries(Path.Combine(UserFolder(), Name)).Select(Path.GetFileName).Order().ShouldBe(["v1", "versions.json"]);
    }

    [Fact]
    public async Task CheckDraft_hands_the_check_a_folder_named_after_the_mod_inside_a_tmp_folder()
    {
        Draft();
        string? staged = null;

        await _store.CheckDraftAsync(User, Session, Name, (copy, _) =>
        {
            staged = copy;
            File.Exists(Path.Combine(copy, "mod.json")).ShouldBeTrue();
            return Task.FromResult<JsonElement?>(null);
        });

        ShouldBeStagedAs(staged, Path.Combine(UserFolder(), Name));
        Directory.GetFileSystemEntries(Path.Combine(UserFolder(), Name)).ShouldBeEmpty();
    }

    [Fact]
    public async Task No_tmp_folder_is_left_after_a_refused_check_or_a_check_that_throws()
    {
        Draft();
        var mod = Path.Combine(UserFolder(), Name);

        await Should.ThrowAsync<ModStoreException>(() => Keep(check: Report("""{"ok":false,"errors":[{"message":"no"}]}""")));
        Directory.GetFileSystemEntries(mod).ShouldBeEmpty();
        await Should.ThrowAsync<InvalidOperationException>(() => Keep(check: (_, _) => throw new InvalidOperationException("boom")));
        Directory.GetFileSystemEntries(mod).ShouldBeEmpty();
        await Should.ThrowAsync<InvalidOperationException>(
            () => _store.CheckDraftAsync(User, Session, Name, (_, _) => throw new InvalidOperationException("boom")));
        Directory.GetFileSystemEntries(mod).ShouldBeEmpty();
    }

    [Fact]
    public async Task Keep_removes_a_nested_staging_folder_a_crash_left_behind()
    {
        Draft();
        var mod = Path.Combine(UserFolder(), Name);
        var leftover = Path.Combine(mod, "keep.0123456789abcdef.tmp", Name);
        Directory.CreateDirectory(Path.Combine(leftover, "deep"));
        File.WriteAllText(Path.Combine(leftover, "deep", "x.txt"), "x");
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(leftover)!, DateTime.UtcNow.AddHours(-2));

        await Keep();

        Directory.GetFileSystemEntries(mod).Select(Path.GetFileName).Order().ShouldBe(["v1", "versions.json"]);
    }

    [Fact]
    public async Task A_draft_changed_while_checked_is_still_refused_with_the_nested_layout()
    {
        var folder = Draft();

        var error = await Should.ThrowAsync<ModStoreException>(() => Keep(check: (_, _) =>
        {
            File.WriteAllText(Path.Combine(folder, "mod.ts"), "// changed\n");
            return Task.FromResult<JsonElement?>(null);
        }));

        error.Message.ShouldContain("changed");
        Directory.GetFileSystemEntries(Path.Combine(UserFolder(), Name)).ShouldBeEmpty();
    }

    // ── Listing draft sessions ──────────────────────────────────────────

    [Fact]
    public async Task No_draft_sessions_when_there_are_no_drafts()
    {
        (await _store.ListDraftSessionsAsync(User)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Draft_sessions_are_listed_in_ordinal_order()
    {
        Draft("ses_b");
        Draft("ses_a");
        Draft("ses_C");

        (await _store.ListDraftSessionsAsync(User)).ShouldBe(["ses_C", "ses_a", "ses_b"]);
    }

    [Fact]
    public async Task Draft_sessions_skip_files_and_names_that_are_not_session_ids()
    {
        Draft("ses_a");
        var drafts = _store.DraftsRoot(User);
        File.WriteAllText(Path.Combine(drafts, "ses_file"), "x");
        Directory.CreateDirectory(Path.Combine(drafts, "not a session"));
        Directory.CreateDirectory(Path.Combine(drafts, "..x"));

        (await _store.ListDraftSessionsAsync(User)).ShouldBe(["ses_a"]);
    }

    [Trait("Category", "ModsFileSafety")]
    [Fact]
    public async Task Draft_sessions_skip_a_linked_session_folder()
    {
        if (OperatingSystem.IsWindows())
            return;
        Draft("ses_a");
        Directory.CreateSymbolicLink(Path.Combine(_store.DraftsRoot(User), "ses_link"), Outside());

        (await _store.ListDraftSessionsAsync(User)).ShouldBe(["ses_a"]);
    }

    [Fact]
    public async Task The_host_folder_is_neither_a_kept_mod_nor_a_draft_session()
    {
        Draft("ses_a");
        await Keep("ses_a");
        var host = _store.HostFolder(User);
        Directory.CreateDirectory(Path.Combine(host, "sub", "deeper"));
        File.WriteAllText(Path.Combine(host, "mod.json"), Manifest);
        File.WriteAllText(Path.Combine(host, "sub", "versions.json"), "{}");
        Directory.CreateDirectory(Path.Combine(host, "ses_hidden", Name));

        (await _store.ListAsync(User)).Select(h => h.Name).ShouldBe([Name]);
        (await _store.ListDraftSessionsAsync(User)).ShouldBeEmpty();
    }

    // ── Staging a draft for the mod host ────────────────────────────────

    private string Destination() => Path.Combine(_root, "stage", Guid.NewGuid().ToString("N"), "out");

    [Fact]
    public async Task StageDraft_copies_files_and_folders_byte_for_byte()
    {
        var folder = Draft(code: "// staged\n");
        Directory.CreateDirectory(Path.Combine(folder, "pages", "deep"));
        byte[] bytes = [0, 1, 2, 255, 254, 10, 13];
        File.WriteAllBytes(Path.Combine(folder, "pages", "deep", "blob.bin"), bytes);
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        var destination = Destination();

        await _store.StageDraftAsync(User, Session, Name, destination);

        File.ReadAllText(Path.Combine(destination, "mod.ts")).ShouldBe("// staged\n");
        File.ReadAllText(Path.Combine(destination, "mod.json")).ShouldBe(Manifest);
        File.ReadAllBytes(Path.Combine(destination, "pages", "deep", "blob.bin")).ShouldBe(bytes);
        Directory.Exists(Path.Combine(destination, "empty")).ShouldBeTrue();
        Directory.Exists(folder).ShouldBeTrue();
    }

    [Fact]
    public async Task StageDraft_refuses_a_destination_that_exists_and_leaves_it_alone()
    {
        Draft();
        var destination = Destination();
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "mine.txt"), "mine");

        await Should.ThrowAsync<ModStoreException>(() => _store.StageDraftAsync(User, Session, Name, destination));

        File.ReadAllText(Path.Combine(destination, "mine.txt")).ShouldBe("mine");
        Directory.GetFileSystemEntries(destination).Length.ShouldBe(1);
    }

    [Fact]
    public async Task StageDraft_without_a_draft_throws_and_writes_nothing()
    {
        var destination = Destination();

        await Should.ThrowAsync<ModStoreException>(() => _store.StageDraftAsync(User, Session, Name, destination));

        Directory.Exists(destination).ShouldBeFalse();
    }

    [Fact]
    public async Task StageDraft_validates_the_names()
    {
        await Should.ThrowAsync<ModStoreException>(() => _store.StageDraftAsync(User, Session, "Bad Name", Destination()));
        await Should.ThrowAsync<ModStoreException>(() => _store.StageDraftAsync(User, "../x", Name, Destination()));
    }

    [Trait("Category", "ModsFileSafety")]
    [Fact]
    public async Task StageDraft_refuses_a_draft_holding_a_link_to_a_file_or_a_folder()
    {
        if (OperatingSystem.IsWindows())
            return;
        var folder = Draft();
        var outside = Outside();
        var destination = Destination();

        File.CreateSymbolicLink(Path.Combine(folder, "file-link"), Path.Combine(outside, "secret.txt"));
        var fileError = await Should.ThrowAsync<ModStoreException>(() => _store.StageDraftAsync(User, Session, Name, destination));
        File.Delete(Path.Combine(folder, "file-link"));
        Directory.CreateSymbolicLink(Path.Combine(folder, "folder-link"), outside);
        var folderError = await Should.ThrowAsync<ModStoreException>(() => _store.StageDraftAsync(User, Session, Name, destination));

        fileError.Message.ShouldContain("link", Case.Insensitive);
        folderError.Message.ShouldContain("link", Case.Insensitive);
        Directory.Exists(destination).ShouldBeFalse();
        File.ReadAllText(Path.Combine(outside, "secret.txt")).ShouldBe("outside");
    }

    [Trait("Category", "ModsFileSafety")]
    [Fact]
    public async Task StageDraft_refuses_a_named_pipe_without_blocking_and_leaves_nothing()
    {
        if (OperatingSystem.IsWindows())
            return;
        var folder = Draft();
        MakeFifo(Path.Combine(folder, "pipe"));
        var destination = Destination();

        await Should.ThrowAsync<ModStoreException>(() => _store.StageDraftAsync(User, Session, Name, destination).WaitAsync(Patience));

        Directory.Exists(destination).ShouldBeFalse();
    }
}
