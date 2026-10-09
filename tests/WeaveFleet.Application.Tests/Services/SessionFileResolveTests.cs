using Shouldly;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>Paths named in a reply, as files in the session's folder: <see cref="SessionOrchestrator.ResolveSessionFilesAsync"/>.</summary>
public sealed class SessionFileResolveTests : IDisposable
{
    private const string SessionId = "sess-links";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fleet-links-{Guid.NewGuid():N}");
    private readonly string _directory;
    private readonly SessionOrchestratorBuilder _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
    private readonly SessionOrchestrator _sut;

    public SessionFileResolveTests()
    {
        _directory = Path.Combine(_root, "harbor-api");
        Directory.CreateDirectory(Path.Combine(_directory, "src", "billing"));
        File.WriteAllText(Path.Combine(_directory, "src", "billing", "tax.ts"), "export const rate = 0.2;\n");
        File.WriteAllText(Path.Combine(_directory, "README.md"), "# harbor-api\n");
        File.WriteAllText(Path.Combine(_root, "outside.ts"), "export {};\n");
        _builder.SessionRepository.Seed(new Session
        {
            Id = SessionId,
            InstanceId = "inst-links",
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

    private async Task<IReadOnlyList<ResolvedSessionFile>> ResolveAsync(params string[] paths)
    {
        var result = await _sut.ResolveSessionFilesAsync(SessionId, paths);
        result.IsSuccess.ShouldBeTrue();
        return result.Value;
    }

    [Theory]
    [InlineData("src/billing/tax.ts")]
    [InlineData("./src/billing/tax.ts")]
    [InlineData(@"src\billing\tax.ts")]
    public async Task a_relative_path_resolves_to_the_file_from_the_folder(string path)
    {
        var file = (await ResolveAsync(path)).ShouldHaveSingleItem();

        file.Path.ShouldBe(path);
        file.RelativePath.ShouldBe("src/billing/tax.ts");
    }

    [Fact]
    public async Task an_absolute_path_inside_the_folder_resolves_to_the_same_file()
    {
        var absolute = Path.Combine(_directory, "src", "billing", "tax.ts");

        var file = (await ResolveAsync(absolute)).ShouldHaveSingleItem();

        file.Path.ShouldBe(absolute);
        file.RelativePath.ShouldBe("src/billing/tax.ts");
    }

    [Fact]
    public async Task a_name_at_the_root_of_the_folder_resolves()
    {
        (await ResolveAsync("README.md")).ShouldHaveSingleItem().RelativePath.ShouldBe("README.md");
    }

    [Theory]
    [InlineData("src/billing/legacy-rates.ts")]
    [InlineData("tax.ts")]
    [InlineData("src/billing")]
    [InlineData("../outside.ts")]
    [InlineData("~/outside.ts")]
    [InlineData("")]
    public async Task missing_files_folders_and_paths_outside_the_folder_are_left_out(string path)
    {
        (await ResolveAsync(path)).ShouldBeEmpty();
    }

    [Fact]
    public async Task an_absolute_path_outside_the_folder_is_left_out()
    {
        (await ResolveAsync(Path.Combine(_root, "outside.ts"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task only_the_files_among_many_paths_come_back_once_each()
    {
        var files = await ResolveAsync("README.md", "src/billing/missing.ts", "README.md", "src/billing/tax.ts");

        files.Select(f => f.RelativePath).ShouldBe(["README.md", "src/billing/tax.ts"]);
    }

    [Fact]
    public async Task an_unknown_session_is_not_found()
    {
        var result = await _sut.ResolveSessionFilesAsync("sess-unknown", ["README.md"]);

        result.IsFailure.ShouldBeTrue();
    }
}
