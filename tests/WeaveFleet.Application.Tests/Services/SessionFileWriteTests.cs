using System.Text;
using Shouldly;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>Saving a file from the editor: <see cref="SessionOrchestrator.WriteSessionFileAsync"/>.</summary>
public sealed class SessionFileWriteTests : IDisposable
{
    private const string SessionId = "sess-editor";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fleet-editor-{Guid.NewGuid():N}");
    private readonly string _directory;
    private readonly SessionOrchestratorBuilder _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
    private readonly SessionOrchestrator _sut;

    public SessionFileWriteTests()
    {
        _directory = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(_directory, "src"));
        _builder.SessionRepository.Seed(new Session
        {
            Id = SessionId,
            InstanceId = "inst-editor",
            Directory = _directory,
            LifecycleStatus = "running",
            RetentionStatus = "active",
            RuntimeMode = "manual",
            ActivityStatus = "idle",
            UserId = "user-1",
        });
        _sut = _builder.Build();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string WriteFile(string relativePath, byte[] bytes)
    {
        var path = Path.Combine(_directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string WriteFile(string relativePath, string text) => WriteFile(relativePath, Encoding.UTF8.GetBytes(text));

    private async Task<string> ReadHashAsync(string relativePath)
    {
        var read = await _sut.ReadSessionFileAsync(SessionId, relativePath);
        read.IsSuccess.ShouldBeTrue();
        return read.Value.Hash.ShouldNotBeNull();
    }

    [Fact]
    public async Task read_returns_the_sha256_of_the_file_bytes()
    {
        WriteFile("src/app.ts", "const one = 1;\n");

        var hash = await ReadHashAsync("src/app.ts");

        hash.ShouldBe(SessionOrchestrator.HashFileBytes(Encoding.UTF8.GetBytes("const one = 1;\n")));
        hash.Length.ShouldBe(64);
    }

    [Fact]
    public async Task save_writes_the_content_returns_the_new_hash_and_tells_other_views()
    {
        var path = WriteFile("src/app.ts", "const one = 1;\n");
        var hash = await ReadHashAsync("src/app.ts");

        var result = await _sut.WriteSessionFileAsync(SessionId, "src/app.ts", "const one = 111;\n", hash);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Saved.ShouldBeTrue();
        (await File.ReadAllTextAsync(path)).ShouldBe("const one = 111;\n");
        result.Value.Hash.ShouldBe(await ReadHashAsync("src/app.ts"));

        var broadcast = _builder.EventBroadcaster.Broadcasts.ShouldHaveSingleItem();
        broadcast.Topic.ShouldBe($"session:{SessionId}");
        broadcast.Type.ShouldBe("files.changed");
        broadcast.UserId.ShouldBe("user-1");
        var changed = broadcast.DomainEvent.ShouldBeOfType<FilesChanged>();
        changed.Payload.Files.ShouldHaveSingleItem().Path.ShouldBe("src/app.ts");
        broadcast.Payload.GetProperty("files")[0].GetProperty("path").GetString().ShouldBe("src/app.ts");
    }

    [Fact]
    public async Task a_stale_hash_is_a_conflict_with_the_current_content_and_nothing_is_written()
    {
        var path = WriteFile("src/app.ts", "const one = 1;\n");
        var hash = await ReadHashAsync("src/app.ts");
        await File.WriteAllTextAsync(path, "const one = 2; // the agent\n");

        var result = await _sut.WriteSessionFileAsync(SessionId, "src/app.ts", "const one = 111;\n", hash);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Saved.ShouldBeFalse();
        result.Value.CurrentContent.ShouldBe("const one = 2; // the agent\n");
        result.Value.Hash.ShouldBe(await ReadHashAsync("src/app.ts"));
        (await File.ReadAllTextAsync(path)).ShouldBe("const one = 2; // the agent\n");
        _builder.EventBroadcaster.Broadcasts.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData(".")]
    public async Task paths_outside_the_session_directory_are_refused(string path)
    {
        File.WriteAllText(Path.Combine(_root, "outside.txt"), "outside");

        var result = await _sut.WriteSessionFileAsync(SessionId, path, "changed", "any");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldStartWith("Validation.");
        File.ReadAllText(Path.Combine(_root, "outside.txt")).ShouldBe("outside");
    }

    [Fact]
    public async Task a_symlink_to_a_file_outside_the_session_is_refused()
    {
        if (OperatingSystem.IsWindows())
            return;

        var outside = Path.Combine(_root, "secret.txt");
        File.WriteAllText(outside, "secret");
        File.CreateSymbolicLink(Path.Combine(_directory, "src", "link.txt"), outside);

        var result = await _sut.WriteSessionFileAsync(
            SessionId, "src/link.txt", "changed", SessionOrchestrator.HashFileBytes(Encoding.UTF8.GetBytes("secret")));

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("outside");
        File.ReadAllText(outside).ShouldBe("secret");
    }

    [Fact]
    public async Task a_file_under_a_symlinked_folder_outside_the_session_is_refused()
    {
        if (OperatingSystem.IsWindows())
            return;

        var outsideDir = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "a.txt"), "a");
        Directory.CreateSymbolicLink(Path.Combine(_directory, "linked"), outsideDir);

        var result = await _sut.WriteSessionFileAsync(
            SessionId, "linked/a.txt", "changed", SessionOrchestrator.HashFileBytes(Encoding.UTF8.GetBytes("a")));

        result.IsFailure.ShouldBeTrue();
        File.ReadAllText(Path.Combine(outsideDir, "a.txt")).ShouldBe("a");
    }

    [Fact]
    public async Task a_symlink_inside_the_session_can_be_saved_through()
    {
        if (OperatingSystem.IsWindows())
            return;

        var real = WriteFile("src/real.ts", "x");
        File.CreateSymbolicLink(Path.Combine(_directory, "src", "alias.ts"), real);
        var hash = await ReadHashAsync("src/alias.ts");

        var result = await _sut.WriteSessionFileAsync(SessionId, "src/alias.ts", "y", hash);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Saved.ShouldBeTrue();
        File.ReadAllText(real).ShouldBe("y");
        new FileInfo(Path.Combine(_directory, "src", "alias.ts")).LinkTarget.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(".git/config")]
    [InlineData("sub/.git/HEAD")]
    [InlineData(".GIT/config")]
    public async Task files_under_git_are_refused(string path)
    {
        WriteFile(path, "[core]\n");
        var hash = SessionOrchestrator.HashFileBytes(Encoding.UTF8.GetBytes("[core]\n"));

        var result = await _sut.WriteSessionFileAsync(SessionId, path, "broken", hash);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain(".git");
        File.ReadAllText(Path.Combine(_directory, path)).ShouldBe("[core]\n");
    }

    [Fact]
    public async Task a_missing_file_is_not_found_and_not_created()
    {
        var result = await _sut.WriteSessionFileAsync(SessionId, "src/new.ts", "new", "any");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldEndWith(".NotFound");
        File.Exists(Path.Combine(_directory, "src", "new.ts")).ShouldBeFalse();
    }

    [Fact]
    public async Task content_over_512_KB_is_refused()
    {
        WriteFile("src/app.ts", "x");
        var hash = await ReadHashAsync("src/app.ts");

        var result = await _sut.WriteSessionFileAsync(SessionId, "src/app.ts", new string('a', 512 * 1024 + 1), hash);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("512 KB");
        File.ReadAllText(Path.Combine(_directory, "src", "app.ts")).ShouldBe("x");
    }

    [Fact]
    public async Task a_file_over_512_KB_on_disk_is_refused()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('a', 512 * 1024 + 1));
        WriteFile("big.txt", bytes);

        var result = await _sut.WriteSessionFileAsync(SessionId, "big.txt", "small", SessionOrchestrator.HashFileBytes(bytes));

        result.IsFailure.ShouldBeTrue();
        new FileInfo(Path.Combine(_directory, "big.txt")).Length.ShouldBe(bytes.Length);
    }

    [Theory]
    [InlineData(new byte[] { 0x50, 0x4E, 0x47, 0x00, 0x01 })]
    [InlineData(new byte[] { 0x61, 0xC3, 0x28 })]
    public async Task a_binary_or_non_utf8_file_is_refused(byte[] bytes)
    {
        WriteFile("image.bin", bytes);

        var result = await _sut.WriteSessionFileAsync(SessionId, "image.bin", "text", SessionOrchestrator.HashFileBytes(bytes));

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("Binary");
        File.ReadAllBytes(Path.Combine(_directory, "image.bin")).ShouldBe(bytes);
    }

    [Fact]
    public async Task crlf_and_a_bom_come_back_byte_for_byte()
    {
        var original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("one\r\ntwo\r\n")).ToArray();
        var path = WriteFile("win.cs", original);

        var read = await _sut.ReadSessionFileAsync(SessionId, "win.cs");
        read.Value.Content.ShouldBe("\uFEFFone\r\ntwo\r\n");

        // The editor sends back the text it was given, BOM included.
        var edited = read.Value.Content!.Replace("two", "TWO");
        var result = await _sut.WriteSessionFileAsync(SessionId, "win.cs", edited, read.Value.Hash);

        result.Value.Saved.ShouldBeTrue();
        File.ReadAllBytes(path).ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("one\r\nTWO\r\n")).ToArray());
    }

    [Fact]
    public async Task saving_unchanged_text_leaves_the_bytes_as_they_were()
    {
        var original = Encoding.UTF8.GetBytes("no newline at the end\r\nmixed\nendings");
        var path = WriteFile("mixed.txt", original);
        var read = await _sut.ReadSessionFileAsync(SessionId, "mixed.txt");

        var result = await _sut.WriteSessionFileAsync(SessionId, "mixed.txt", read.Value.Content, read.Value.Hash);

        result.Value.Saved.ShouldBeTrue();
        result.Value.Hash.ShouldBe(read.Value.Hash);
        File.ReadAllBytes(path).ShouldBe(original);
    }

    [Fact]
    public async Task the_file_keeps_its_permissions_and_inode()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = WriteFile("run.sh", "#!/bin/sh\necho hi\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
        var hardLink = Path.Combine(_root, "run-hardlink.sh");
        // A hard link shares the inode: it sees the write only if the file was written in place.
        LinkFile(path, hardLink);
        var hash = await ReadHashAsync("run.sh");

        var result = await _sut.WriteSessionFileAsync(SessionId, "run.sh", "#!/bin/sh\necho bye\n", hash);

        result.Value.Saved.ShouldBeTrue();
        File.GetUnixFileMode(path).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
        File.ReadAllText(hardLink).ShouldBe("#!/bin/sh\necho bye\n");
    }

    [Fact]
    public async Task a_missing_base_hash_is_refused()
    {
        WriteFile("src/app.ts", "x");

        var result = await _sut.WriteSessionFileAsync(SessionId, "src/app.ts", "y", baseHash: null);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldStartWith("Validation.");
    }

    [Fact]
    public async Task an_unknown_session_is_not_found()
    {
        var result = await _sut.WriteSessionFileAsync("nope", "src/app.ts", "y", "hash");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldEndWith(".NotFound");
    }

    [Fact]
    public async Task two_saves_from_the_same_read_only_let_the_first_one_win()
    {
        WriteFile("src/app.ts", "base\n");
        var hash = await ReadHashAsync("src/app.ts");

        var results = await Task.WhenAll(
            _sut.WriteSessionFileAsync(SessionId, "src/app.ts", "first\n", hash),
            _sut.WriteSessionFileAsync(SessionId, "src/app.ts", "second\n", hash));

        results.Count(r => r.Value.Saved).ShouldBe(1);
        results.Count(r => !r.Value.Saved).ShouldBe(1);
        var loser = results.Single(r => !r.Value.Saved).Value;
        loser.CurrentContent.ShouldBe(File.ReadAllText(Path.Combine(_directory, "src", "app.ts")));
    }

    private static void LinkFile(string existing, string link)
    {
        using var process = System.Diagnostics.Process.Start("ln", [existing, link]);
        process.WaitForExit();
        process.ExitCode.ShouldBe(0);
    }
}
