using System.Diagnostics;
using System.Text;
using WeaveFleet.Application.Mods;
using WeaveFleet.Infrastructure.Mods;

namespace WeaveFleet.Infrastructure.Tests.Mods;

/// <summary>Writing a draft's files for the agent: only inside the draft, never through a link, within the limits, all or nothing.</summary>
public sealed class FileModVersionStoreDraftWriteTests : IDisposable
{
    private const string User = "local-user";
    private const string Name = "test-chips";
    private const string Session = "ses_alpha";
    private const string Manifest = """{"name":"test-chips","version":"0.1.0","description":"Shows chips.","hooks":"mod.ts"}""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fleet-mods-write-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"fleet-mods-write-outside-{Guid.NewGuid():N}");
    private readonly FileModVersionStore _store;

    public FileModVersionStoreDraftWriteTests()
    {
        _store = new FileModVersionStore(_root);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "outside");
    }

    public void Dispose()
    {
        _store.Dispose();
        Delete(_root);
        Delete(_outside);
    }

    private static void Delete(string folder)
    {
        if (!Directory.Exists(folder))
            return;
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
                entry.Delete();
            else if (entry is DirectoryInfo directory)
                Delete(directory.FullName);
            else
                entry.Delete();
        }

        Directory.Delete(folder);
    }

    private string DraftFolder => _store.DraftFolder(User, Session, Name);

    private Task<ModDraft> Write(params (string Path, string Content)[] files)
        => _store.WriteDraftFilesAsync(User, Session, Name, files.Select(f => new ModFile(f.Path, f.Content)).ToList());

    private async Task<ModStoreException> Refused(params (string Path, string Content)[] files)
        => await Should.ThrowAsync<ModStoreException>(() => Write(files));

    private static void MakeFifo(string path)
    {
        using var process = Process.Start("mkfifo", path)!;
        process.WaitForExit();
        process.ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task Writing_creates_the_draft_and_returns_it_with_its_manifest()
    {
        var draft = await Write(("mod.json", Manifest), ("mod.ts", "export default {};\n"));

        draft.SessionId.ShouldBe(Session);
        draft.Name.ShouldBe(Name);
        draft.Folder.ShouldBe(DraftFolder);
        draft.Manifest.ShouldBe(new ModManifest("test-chips", "0.1.0", "Shows chips.", "mod.ts"));
        File.ReadAllText(Path.Combine(DraftFolder, "mod.ts")).ShouldBe("export default {};\n");
        (await _store.GetDraftAsync(User, Session, Name)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Writing_replaces_the_named_files_and_leaves_the_others()
    {
        await Write(("mod.json", Manifest), ("mod.ts", "one"), ("notes.md", "keep me"));
        await Write(("mod.ts", "two"));

        File.ReadAllText(Path.Combine(DraftFolder, "mod.ts")).ShouldBe("two");
        File.ReadAllText(Path.Combine(DraftFolder, "notes.md")).ShouldBe("keep me");
        Directory.GetFiles(DraftFolder).Length.ShouldBe(3);
    }

    [Fact]
    public async Task Writing_creates_subfolders()
    {
        await Write(("mod.json", Manifest), ("pages/deep/index.html", "<p>hi</p>"));

        File.ReadAllText(Path.Combine(DraftFolder, "pages", "deep", "index.html")).ShouldBe("<p>hi</p>");
    }

    [Fact]
    public async Task Content_is_utf8_without_a_byte_order_mark()
    {
        await Write(("mod.ts", "caf\u00e9"));

        File.ReadAllBytes(Path.Combine(DraftFolder, "mod.ts")).ShouldBe(new UTF8Encoding(false).GetBytes("caf\u00e9"));
    }

    [Fact]
    public async Task Writing_leaves_no_temporary_files_behind()
    {
        await Write(("mod.ts", "one"), ("a/b.txt", "two"));
        await Write(("mod.ts", "three"));

        Directory.GetFiles(DraftFolder, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Order().ToArray().ShouldBe(["b.txt", "mod.ts"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\\b.ts")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/x.ts")]
    [InlineData("C:x.ts")]
    [InlineData("../x")]
    [InlineData("a/../../x")]
    [InlineData("a/../x")]
    [InlineData("./x")]
    [InlineData("a/./x")]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData("a\0b")]
    [InlineData("a\nb")]
    [InlineData("a\u001bb")]
    [InlineData("dot.")]
    [InlineData("a/dot./b")]
    [InlineData("space ")]
    [InlineData("con")]
    [InlineData("a/CON.txt")]
    [InlineData("nul.txt")]
    [InlineData("com1")]
    [InlineData("lpt9.log")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    public async Task A_path_that_is_not_a_plain_relative_path_is_refused_and_nothing_is_written(string path)
    {
        var error = await Refused(("mod.ts", "fine"), (path, "x"));

        error.Message.ShouldNotBeEmpty();
        Directory.Exists(DraftFolder).ShouldBeFalse();
        Directory.GetFiles(_outside).Length.ShouldBe(1);
    }

    [Fact]
    public async Task A_path_that_leaves_the_folder_says_where_to_write_instead()
    {
        var error = await Refused(("../x", "x"));

        error.Message.ShouldBe("\"../x\" leaves the mod's folder: use a path inside it, like mod.ts.");
    }

    [Fact]
    public async Task A_path_that_is_too_long_is_refused()
    {
        var error = await Refused((new string('a', 261), "x"));

        error.Message.ShouldContain("too long");
        await Write((new string('a', 200), "x"));
    }

    [Fact]
    public async Task The_same_path_twice_is_refused()
    {
        var error = await Refused(("mod.ts", "one"), ("mod.ts", "two"));

        error.Message.ShouldContain("mod.ts");
        Directory.Exists(DraftFolder).ShouldBeFalse();
    }

    [Fact]
    public async Task Paths_that_differ_only_in_case_are_refused()
    {
        var error = await Refused(("Mod.ts", "one"), ("mod.ts", "two"));

        error.Message.ShouldContain("Mod.ts");
        Directory.Exists(DraftFolder).ShouldBeFalse();
    }

    [Fact]
    public async Task One_bad_path_writes_nothing()
    {
        await Write(("mod.ts", "before"));

        await Refused(("mod.ts", "after"), ("pages/index.html", "x"), ("../x", "x"));

        File.ReadAllText(Path.Combine(DraftFolder, "mod.ts")).ShouldBe("before");
        Directory.Exists(Path.Combine(DraftFolder, "pages")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_file_that_is_a_link_is_refused_and_its_target_is_untouched()
    {
        if (OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(DraftFolder);
        File.CreateSymbolicLink(Path.Combine(DraftFolder, "mod.ts"), Path.Combine(_outside, "secret.txt"));

        var error = await Refused(("other.ts", "x"), ("mod.ts", "overwritten"));

        error.Message.ShouldContain("mod.ts");
        File.ReadAllText(Path.Combine(_outside, "secret.txt")).ShouldBe("outside");
        File.Exists(Path.Combine(DraftFolder, "other.ts")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_subfolder_that_is_a_link_is_refused()
    {
        if (OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(DraftFolder);
        Directory.CreateSymbolicLink(Path.Combine(DraftFolder, "pages"), _outside);

        var error = await Refused(("pages/new.txt", "x"));

        error.Message.ShouldContain("pages");
        Directory.GetFiles(_outside).Length.ShouldBe(1);
    }

    [Fact]
    public async Task A_draft_folder_that_is_a_link_is_refused()
    {
        if (OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(DraftFolder)!);
        Directory.CreateSymbolicLink(DraftFolder, _outside);

        await Refused(("mod.ts", "x"));

        Directory.GetFiles(_outside).Length.ShouldBe(1);
    }

    [Fact]
    public async Task A_session_folder_that_is_a_link_is_refused()
    {
        if (OperatingSystem.IsWindows())
            return;
        var session = Path.GetDirectoryName(DraftFolder)!;
        Directory.CreateDirectory(Path.GetDirectoryName(session)!);
        Directory.CreateSymbolicLink(session, _outside);

        await Refused(("mod.ts", "x"));

        Directory.GetFileSystemEntries(_outside).Length.ShouldBe(1);
    }

    [Fact]
    public async Task A_file_where_a_folder_is_needed_is_refused()
    {
        await Write(("pages", "I am a file"));

        var error = await Refused(("pages/index.html", "x"));

        error.Message.ShouldContain("pages");
    }

    [Fact]
    public async Task A_folder_where_a_file_goes_is_refused()
    {
        await Write(("pages/index.html", "x"));

        var error = await Refused(("pages", "x"));

        error.Message.ShouldContain("pages");
    }

    [Fact]
    public async Task A_named_pipe_in_the_way_is_refused_without_blocking()
    {
        if (OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(DraftFolder);
        MakeFifo(Path.Combine(DraftFolder, "pipe.txt"));

        var refusal = Refused(("pipe.txt", "x"));

        (await Task.WhenAny(refusal, Task.Delay(TimeSpan.FromSeconds(10)))).ShouldBe(refusal);
        (await refusal).Message.ShouldContain("pipe.txt");
    }

    [Fact]
    public async Task A_named_pipe_among_the_files_that_stay_is_refused_without_blocking()
    {
        if (OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(DraftFolder);
        MakeFifo(Path.Combine(DraftFolder, "pipe.txt"));

        var refusal = Refused(("mod.ts", "x"));

        (await Task.WhenAny(refusal, Task.Delay(TimeSpan.FromSeconds(10)))).ShouldBe(refusal);
        (await refusal).Message.ShouldContain("pipe.txt");
        File.Exists(Path.Combine(DraftFolder, "mod.ts")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_link_among_the_files_that_stay_is_refused()
    {
        if (OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(DraftFolder);
        File.CreateSymbolicLink(Path.Combine(DraftFolder, "link.txt"), Path.Combine(_outside, "secret.txt"));

        var error = await Refused(("mod.ts", "x"));

        error.Message.ShouldContain("link.txt");
    }

    [Fact]
    public async Task The_draft_can_hold_the_most_files_but_not_one_more()
    {
        await Write(Enumerable.Range(0, ModStoreLimits.KeptFiles).Select(i => ($"f{i}.txt", "x")).ToArray());

        var error = await Refused(("one-more.txt", "x"));
        error.Message.ShouldContain(ModStoreLimits.KeptFiles.ToString());
        File.Exists(Path.Combine(DraftFolder, "one-more.txt")).ShouldBeFalse();

        await Write(("f0.txt", "replaced"));
    }

    [Fact]
    public async Task New_files_count_against_the_limit_with_the_ones_already_there()
    {
        await Write(Enumerable.Range(0, ModStoreLimits.KeptFiles - 1).Select(i => ($"f{i}.txt", "x")).ToArray());

        await Refused(("a.txt", "x"), ("b.txt", "x"));

        Directory.GetFiles(DraftFolder).Length.ShouldBe(ModStoreLimits.KeptFiles - 1);
    }

    [Fact]
    public async Task The_draft_can_hold_the_most_bytes_but_not_one_more()
    {
        var half = new string('a', (int)(ModStoreLimits.KeptBytes / 2));
        await Write(("a.txt", half), ("b.txt", half));

        var error = await Refused(("c.txt", "x"));
        error.Message.ShouldContain("16 MiB");

        await Write(("a.txt", "small"));
        await Write(("c.txt", half));
    }

    [Fact]
    public async Task Bytes_are_counted_as_utf8()
    {
        var wide = new string('\u00e9', (int)(ModStoreLimits.KeptBytes / 2) + 1);

        await Refused(("a.txt", wide));

        Directory.Exists(DraftFolder).ShouldBeFalse();
    }

    [Fact]
    public async Task A_new_file_that_pushes_past_the_bytes_writes_nothing()
    {
        await Write(("a.txt", "before"));

        await Refused(("a.txt", "after"), ("big.txt", new string('x', (int)ModStoreLimits.KeptBytes + 1)));

        File.ReadAllText(Path.Combine(DraftFolder, "a.txt")).ShouldBe("before");
    }

    [Fact]
    public async Task An_invalid_mod_name_or_session_is_an_argument_error()
    {
        var files = new List<ModFile> { new("mod.ts", "x") };

        await Should.ThrowAsync<ArgumentException>(() => _store.WriteDraftFilesAsync(User, Session, "../evil", files));
        await Should.ThrowAsync<ArgumentException>(() => _store.WriteDraftFilesAsync(User, "../evil", Name, files));
    }
}
