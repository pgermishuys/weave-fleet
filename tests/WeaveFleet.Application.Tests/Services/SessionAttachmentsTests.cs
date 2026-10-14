using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Sessions.Files;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Builders;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>Files attached to a prompt: <see cref="SessionAttachments"/>.</summary>
public sealed class SessionAttachmentsTests : IDisposable
{
    private const string SessionId = "sess-attach";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fleet-attach-{Guid.NewGuid():N}");
    private readonly string _directory;
    private readonly SessionOrchestratorBuilder _builder = new();
    private readonly SessionAttachments _sut;

    public SessionAttachmentsTests()
    {
        _directory = Path.Combine(_root, "harbor-api");
        Directory.CreateDirectory(_directory);
        Seed(SessionId, _directory, "active");
        _sut = new SessionAttachments(_builder.SessionRepository, NullLogger<SessionAttachments>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void Seed(string id, string directory, string retention) => _builder.SessionRepository.Seed(new Session
    {
        Id = id,
        InstanceId = "inst-" + id,
        Directory = directory,
        LifecycleStatus = "running",
        RetentionStatus = retention,
        RuntimeMode = "manual",
        ActivityStatus = "idle",
        UserId = "user-1",
    });

    private Task<Result<SessionAttachment>> SaveAsync(string name, byte[] bytes, long? length = null, string id = SessionId) =>
        _sut.SaveAsync(id, name, "text/plain", new MemoryStream(bytes), length);

    private string[] AttachmentFiles() =>
        Directory.GetFiles(Path.Combine(_directory, ".weave", "attachments"), "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != ".gitignore").ToArray();

    [Fact]
    public async Task writes_the_file_in_a_folder_per_session_and_returns_a_relative_path()
    {
        var result = await SaveAsync("build-output.log", "a\nb\n"u8.ToArray());

        result.IsSuccess.ShouldBeTrue();
        result.Value.Path.ShouldMatch(@"^\.weave/attachments/sess-att/build-output\.log$");
        result.Value.Name.ShouldBe("build-output.log");
        result.Value.Size.ShouldBe(4);
        result.Value.Mime.ShouldBe("text/plain");
        File.ReadAllText(Path.Combine(_directory, result.Value.Path)).ShouldBe("a\nb\n");
    }

    [Fact]
    public async Task a_taken_name_gets_a_number()
    {
        await SaveAsync("a.log", "1"u8.ToArray());
        (await SaveAsync("a.log", "2"u8.ToArray())).Value.Path.ShouldEndWith("/a-2.log");
        (await SaveAsync("a.log", "3"u8.ToArray())).Value.Path.ShouldEndWith("/a-3.log");
        File.ReadAllText(Path.Combine(_directory, ".weave/attachments/sess-att/a.log")).ShouldBe("1");
    }

    [Theory]
    [InlineData("my report (final).pdf", "my-report--final-.pdf")]
    [InlineData("..hidden", "hidden")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\temp\\a.txt", "a.txt")]
    public async Task sanitizes_the_name(string given, string expected)
    {
        var result = await SaveAsync(given, [1]);
        result.Value.Name.ShouldBe(expected);
    }

    [Fact]
    public async Task caps_a_long_name_and_keeps_the_extension()
    {
        var result = await SaveAsync(new string('a', 300) + ".log", [1]);
        result.Value.Name.Length.ShouldBe(100);
        result.Value.Name.ShouldEndWith(".log");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    public async Task refuses_a_blank_name(string name)
    {
        (await SaveAsync(name, [1])).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task writes_the_gitignore_once()
    {
        await SaveAsync("a.txt", [1]);
        var ignore = Path.Combine(_directory, ".weave", "attachments", ".gitignore");
        File.ReadAllText(ignore).ShouldBe("*\n");
        File.WriteAllText(ignore, "kept");

        await SaveAsync("b.txt", [1]);
        File.ReadAllText(ignore).ShouldBe("kept");
    }

    [Theory]
    [InlineData("one\ntwo\n", 2)]
    [InlineData("one\ntwo", 2)]
    [InlineData("one", 1)]
    [InlineData("", 0)]
    public async Task counts_lines(string text, int lines)
    {
        var result = await SaveAsync("a.txt", System.Text.Encoding.UTF8.GetBytes(text));
        result.Value.Lines.ShouldBe(lines);
    }

    [Fact]
    public async Task a_file_with_a_nul_byte_has_no_line_count()
    {
        var result = await SaveAsync("a.bin", [0x50, 0x4b, 0, 3, (byte)'\n']);
        result.Value.Lines.ShouldBeNull();
    }

    [Fact]
    public async Task refuses_a_file_over_the_limit_and_leaves_nothing_behind()
    {
        var result = await SaveAsync("dump.zip", new byte[(25 * 1024 * 1024) + 1]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldBe("dump.zip is 26 MB. The limit is 25 MB.");
        AttachmentFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task a_content_length_over_the_limit_refuses_before_any_write()
    {
        var result = await SaveAsync("dump.zip", [1], length: 61L * 1024 * 1024);

        result.Error.Description.ShouldBe("dump.zip is 61 MB. The limit is 25 MB.");
        Directory.Exists(Path.Combine(_directory, ".weave")).ShouldBeFalse();
    }

    [Fact]
    public async Task refuses_an_archived_session()
    {
        Seed("sess-old", _directory, "archived");
        var result = await SaveAsync("a.txt", [1], id: "sess-old");
        result.Error.Description.ShouldBe("Archived sessions are read-only.");
    }

    [Fact]
    public async Task refuses_a_missing_session_folder()
    {
        Seed("sess-gone", Path.Combine(_root, "gone"), "active");
        (await SaveAsync("a.txt", [1], id: "sess-gone")).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task an_unknown_session_is_not_found()
    {
        var result = await SaveAsync("a.txt", [1], id: "nope");
        result.Error.Code.ShouldBe(FleetError.NotFoundFor("Session", "nope").Code);
    }

    [Fact]
    public async Task refuses_a_linked_weave_folder()
    {
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateSymbolicLink(Path.Combine(_directory, ".weave"), elsewhere);

        var result = await SaveAsync("a.txt", [1]);

        result.IsFailure.ShouldBeTrue();
        Directory.GetFileSystemEntries(elsewhere).ShouldBeEmpty();
    }

    [Fact]
    public async Task refuses_a_linked_session_folder()
    {
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateDirectory(Path.Combine(_directory, ".weave", "attachments"));
        Directory.CreateSymbolicLink(Path.Combine(_directory, ".weave", "attachments", "sess-att"), elsewhere);

        (await SaveAsync("a.txt", [1])).IsFailure.ShouldBeTrue();
        Directory.GetFileSystemEntries(elsewhere).ShouldBeEmpty();
    }
}
