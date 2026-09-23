using WeaveFleet.Infrastructure.Weave;

namespace WeaveFleet.Infrastructure.Tests.Weave;

public sealed class WeaveConfigFolderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"weave-config-folder-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void the_folder_holds_exactly_the_saved_files()
    {
        WeaveConfigFolder.Mirror(_folder, new Dictionary<string, string>
        {
            ["config.weave"] = "agent a {}",
            ["prompts/old.md"] = "old",
            ["weave-opencode.jsonc"] = "{}",
        });

        WeaveConfigFolder.Mirror(_folder, new Dictionary<string, string>
        {
            ["config.weave"] = "agent b {}",
            ["prompts/team.md"] = "Be brief.",
        });

        File.ReadAllText(Path.Combine(_folder, "config.weave")).ShouldBe("agent b {}");
        File.ReadAllText(Path.Combine(_folder, "prompts", "team.md")).ShouldBe("Be brief.");
        File.Exists(Path.Combine(_folder, "prompts", "old.md")).ShouldBeFalse();
        File.Exists(Path.Combine(_folder, "weave-opencode.jsonc")).ShouldBeFalse();
    }

    [Fact]
    public void an_unchanged_file_is_not_rewritten()
    {
        var files = new Dictionary<string, string> { ["config.weave"] = "agent a {}" };
        WeaveConfigFolder.Mirror(_folder, files);
        var path = Path.Combine(_folder, "config.weave");
        var written = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);

        WeaveConfigFolder.Mirror(_folder, files);

        File.GetLastWriteTimeUtc(path).ShouldBe(written);
    }

    [Fact]
    public void files_it_doesnt_own_are_left_alone()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "mine");

        WeaveConfigFolder.Mirror(_folder, new Dictionary<string, string>());

        File.ReadAllText(Path.Combine(_folder, "notes.txt")).ShouldBe("mine");
    }

    [Theory]
    [InlineData("../escape.weave")]
    [InlineData("prompts/../../escape.md")]
    [InlineData("/etc/passwd")]
    public void a_path_outside_the_folder_is_refused(string path)
    {
        Should.Throw<ArgumentException>(() => WeaveConfigFolder.Mirror(_folder, new Dictionary<string, string> { [path] = "x" }));
    }

    [Fact]
    public void each_user_gets_their_own_folder_and_its_path_never_changes()
    {
        var alice = WeaveConfigFolder.ForUser("/data", "alice");

        WeaveConfigFolder.ForUser("/data", "alice").ShouldBe(alice);
        WeaveConfigFolder.ForUser("/data", "bob").ShouldNotBe(alice);
        alice.ShouldStartWith(Path.Combine("/data", "weave-config"));
        WeaveConfigFolder.TrialFor("/data", "alice").ShouldNotBe(alice);
    }
}
