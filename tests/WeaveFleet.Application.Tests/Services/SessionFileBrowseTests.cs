using System.Text;
using Shouldly;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Sessions.Files;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// Browsing, reading and finding files in the session's folder: <see cref="SessionOrchestrator.BrowseSessionDirectoryAsync"/>,
/// <see cref="SessionOrchestrator.ReadSessionFileAsync"/> and <see cref="SessionOrchestrator.FindSessionFilesAsync"/>.
/// </summary>
public sealed class SessionFileBrowseTests : IDisposable
{
    private const string SessionId = "sess-browse";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fleet-browse-{Guid.NewGuid():N}");
    private readonly string _directory;
    private readonly SessionOrchestratorBuilder _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
    private readonly SessionOrchestrator _sut;

    public SessionFileBrowseTests()
    {
        _directory = Path.Combine(_root, "harbor-api");
        Directory.CreateDirectory(Path.Combine(_directory, "src", "billing"));
        Directory.CreateDirectory(Path.Combine(_directory, ".git"));
        Directory.CreateDirectory(Path.Combine(_directory, "Docs"));
        File.WriteAllText(Path.Combine(_directory, "src", "billing", "tax.ts"), "export const rate = 0.2;\n");
        File.WriteAllText(Path.Combine(_directory, "README.md"), "# harbor-api\n");
        File.WriteAllText(Path.Combine(_directory, "a-notes.txt"), "notes\n");
        File.WriteAllText(Path.Combine(_root, "outside.ts"), "export {};\n");
        _builder.SessionRepository.Seed(new Session
        {
            Id = SessionId,
            InstanceId = "inst-browse",
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

    [Fact]
    public async Task browsing_the_folder_lists_folders_first_then_files_alphabetically_without_git()
    {
        var result = await _sut.BrowseSessionDirectoryAsync(SessionId, null);

        result.IsSuccess.ShouldBeTrue();
        result.Value.CurrentPath.ShouldBe(string.Empty);
        result.Value.Entries.Select(e => (e.Name, e.RelativePath, e.IsDirectory)).ShouldBe(
        [
            ("Docs", "Docs", true),
            ("src", "src", true),
            ("a-notes.txt", "a-notes.txt", false),
            ("README.md", "README.md", false),
        ]);
    }

    [Theory]
    [InlineData("src/billing")]
    [InlineData("src/billing/")]
    public async Task browsing_a_subfolder_gives_paths_from_the_session_folder(string path)
    {
        var result = await _sut.BrowseSessionDirectoryAsync(SessionId, path);

        result.IsSuccess.ShouldBeTrue();
        result.Value.CurrentPath.ShouldBe("src/billing");
        result.Value.Entries.ShouldHaveSingleItem().ShouldBe(new BrowseEntry("tax.ts", "src/billing/tax.ts", false));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("src/../../")]
    public async Task browsing_outside_the_folder_is_refused(string path)
    {
        var result = await _sut.BrowseSessionDirectoryAsync(SessionId, path);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldBe("Path traversal is not allowed.");
    }

    [Fact]
    public async Task browsing_a_missing_folder_or_session_fails()
    {
        (await _sut.BrowseSessionDirectoryAsync(SessionId, "nope")).Error.Description.ShouldBe("Directory does not exist.");
        (await _sut.BrowseSessionDirectoryAsync("sess-missing", null)).Error.Code.ShouldEndWith(".NotFound");
    }

    [Fact]
    public async Task reading_a_text_file_gives_its_content_and_hash()
    {
        var result = await _sut.ReadSessionFileAsync(SessionId, "src/billing/tax.ts");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(new ReadFileResult(
            "src/billing/tax.ts",
            "export const rate = 0.2;\n",
            IsBinary: false,
            IsTruncated: false,
            SessionOrchestrator.HashFileBytes(Encoding.UTF8.GetBytes("export const rate = 0.2;\n"))));
    }

    [Fact]
    public async Task reading_a_binary_file_gives_no_content()
    {
        await File.WriteAllBytesAsync(Path.Combine(_directory, "logo.bin"), [0x89, 0x50, 0x00, 0x47]);

        var result = await _sut.ReadSessionFileAsync(SessionId, "logo.bin");

        result.Value.IsBinary.ShouldBeTrue();
        result.Value.Content.ShouldBeNull();
        result.Value.IsTruncated.ShouldBeFalse();
        result.Value.Hash.ShouldBe(SessionOrchestrator.HashFileBytes([0x89, 0x50, 0x00, 0x47]));
    }

    [Fact]
    public async Task reading_a_file_over_the_editable_size_gives_no_content_and_no_hash()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "big.log"), new string('x', SessionFiles.MaxEditableFileBytes + 1));

        var result = await _sut.ReadSessionFileAsync(SessionId, "big.log");

        result.Value.ShouldBe(new ReadFileResult("big.log", Content: null, IsBinary: false, IsTruncated: true));
    }

    [Fact]
    public async Task reading_outside_the_folder_or_a_missing_file_fails()
    {
        (await _sut.ReadSessionFileAsync(SessionId, "../outside.ts")).Error.Description.ShouldBe("Path traversal is not allowed.");
        (await _sut.ReadSessionFileAsync(SessionId, "missing.ts")).Error.Code.ShouldEndWith(".NotFound");
    }

    [Fact]
    public async Task finding_files_in_a_folder_that_is_gone_gives_none()
    {
        Directory.Delete(_directory, recursive: true);

        var result = await _sut.FindSessionFilesAsync(SessionId, "tax");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeEmpty();
    }
}
