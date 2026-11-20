using Shouldly;
using WeaveFleet.Application.TerminalSessions;

namespace WeaveFleet.Application.Tests.TerminalSessions;

/// <summary>Where Claude Code keeps a session's transcript, and which paths Fleet will read as one.</summary>
public sealed class ClaudeTranscriptLocatorTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), $"fleet-claude-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_config))
            Directory.Delete(_config, recursive: true);
    }

    private string Put(string slug, string name)
    {
        var directory = Path.Combine(_config, "projects", slug);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "{}\n");
        return path;
    }

    [Theory]
    [InlineData("/home/ada/orchard", "-home-ada-orchard")]
    [InlineData("/home/ada/.cache/orchard_2", "-home-ada--cache-orchard-2")]
    [InlineData(@"C:\work\orchard", "C--work-orchard")]
    public void A_folders_slug_turns_every_other_character_into_a_dash(string folder, string slug)
        => ClaudeTranscriptLocator.Slug(folder).ShouldBe(slug);

    [Fact]
    public void It_finds_the_transcript_in_the_folders_own_project_and_otherwise_anywhere()
    {
        var own = Put(ClaudeTranscriptLocator.Slug("/work/orchard"), "abc.jsonl");
        var elsewhere = Put("-some-other-slug", "def.jsonl");

        ClaudeTranscriptLocator.Find("abc", "/work/orchard", _config).ShouldBe(own);
        ClaudeTranscriptLocator.Find("def", "/work/orchard", _config).ShouldBe(elsewhere);
        ClaudeTranscriptLocator.Find("ghi", "/work/orchard", _config).ShouldBeNull();
    }

    [Fact]
    public void A_session_id_that_is_a_path_finds_nothing()
    {
        Put(ClaudeTranscriptLocator.Slug("/work/orchard"), "abc.jsonl");

        ClaudeTranscriptLocator.Find("../projects/-work-orchard/abc", "/work/orchard", _config).ShouldBeNull();
    }

    [Fact]
    public void The_newest_transcript_in_a_folder_is_the_one_written_last()
    {
        var older = Put(ClaudeTranscriptLocator.Slug("/work/orchard"), "old.jsonl");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-1));
        var newer = Put(ClaudeTranscriptLocator.Slug("/work/orchard"), "new.jsonl");

        ClaudeTranscriptLocator.Newest("/work/orchard", _config).ShouldBe(newer);
    }

    [Fact]
    public void Only_a_path_named_for_the_session_is_a_transcript_of_it()
    {
        ClaudeTranscriptLocator.IsTranscriptOf("/home/ada/.claude/projects/x/abc.jsonl", "abc").ShouldBeTrue();
        ClaudeTranscriptLocator.IsTranscriptOf("/home/ada/.ssh/id_ed25519", "abc").ShouldBeFalse();
        ClaudeTranscriptLocator.IsTranscriptOf("abc.jsonl", "abc").ShouldBeFalse();
        ClaudeTranscriptLocator.IsTranscriptOf(null, "abc").ShouldBeFalse();
    }
}
