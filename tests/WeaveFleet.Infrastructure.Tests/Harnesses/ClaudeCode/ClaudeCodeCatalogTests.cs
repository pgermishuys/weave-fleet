using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

/// <summary>The models the picker offers for Claude Code: what Claude Code's <c>initialize</c> answer lists.</summary>
public sealed class ClaudeCodeCatalogTests
{
    // Claude Code 2.1.290's answer, its model list cut to a few (one from a gateway).
    private static readonly JsonElement Initialize = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ClaudeCode", "initialize.json")))
        .RootElement.GetProperty("response").GetProperty("response").Clone();

    [Fact]
    public void The_models_are_Claude_Codes_own_with_the_efforts_each_takes()
    {
        var provider = ClaudeCodeCatalog.FromInitialize(Initialize).ShouldNotBeNull().ShouldHaveSingleItem();

        provider.Id.ShouldBe(ClaudeCodeCatalog.ProviderId);
        // "default" is Fleet's own Default.
        provider.Models.Select(m => m.Id).ShouldBe(["opus", "claude-fable-5-1", "sonnet", "haiku", "claude-opus-4-6"]);
        provider.Models[0].Name.ShouldBe("Opus");
        provider.Models[0].Variants.ShouldBe(["low", "medium", "high", "xhigh", "max"]);
        provider.Models.Single(m => m.Id == "claude-opus-4-6").Variants.ShouldBe(["low", "medium", "high", "max"]);
        provider.Models.Single(m => m.Id == "haiku").Variants.ShouldBeNull();
    }

    [Fact]
    public async Task A_folders_list_is_asked_for_once_and_kept_for_a_while()
    {
        var time = new FakeTimeProvider();
        var asked = 0;
        var catalog = new ClaudeCodeCatalog(new ClaudeCodeOptions(), NullLoggerFactory.Instance)
        {
            Time = time,
            Ask = (_, _, _) => { asked++; return Task.FromResult<JsonElement?>(Initialize); },
        };

        (await catalog.GetProvidersAsync("/work", CancellationToken.None))[0].Models.Count.ShouldBe(5);
        await catalog.GetProvidersAsync("/work", CancellationToken.None);
        asked.ShouldBe(1);

        time.Advance(ClaudeCodeCatalog.CacheFor + TimeSpan.FromSeconds(1));
        await catalog.GetProvidersAsync("/work", CancellationToken.None);
        asked.ShouldBe(2);
    }

    [Fact]
    public async Task When_Claude_Code_cant_say_the_picker_offers_its_aliases_and_asks_again_next_time()
    {
        var asked = 0;
        var catalog = new ClaudeCodeCatalog(new ClaudeCodeOptions(), NullLoggerFactory.Instance)
        {
            Ask = (_, _, _) => ++asked == 1 ? throw new IOException("claude not found") : Task.FromResult<JsonElement?>(Initialize),
        };

        (await catalog.GetProvidersAsync("/work", CancellationToken.None)).ShouldBe(ClaudeCodeCatalog.Fallback);
        (await catalog.GetProvidersAsync("/work", CancellationToken.None))[0].Models.Count.ShouldBe(5);
    }

    // Commands from Claude Code 2.1.290's answers, descriptions cut, with Fleet's built-in skills in an --add-dir folder
    // (which has no "builtin").
    private static readonly JsonElement WithCommands = JsonDocument.Parse("""
        {"commands":[
          {"name":"fleet-walkthrough","description":"Walk the user through a change","argumentHint":""},
          {"name":"code-review","description":"Review the current diff","argumentHint":"[level]","builtin":true},
          {"name":"clear","description":"Start a new session with empty context","argumentHint":"[name]","aliases":["reset","new"],"builtin":true},
          {"name":"__remote-workflow","description":"","builtin":true},
          {"name":"tidy","description":"A project command"},
          {"name":"tidy","description":"Listed twice"},
          {"description":"No name"}],
         "models":[]}
        """).RootElement.Clone();

    [Fact]
    public void The_commands_say_which_are_Claude_Codes_own_and_leave_out_its_internals()
    {
        ClaudeCodeCatalog.CommandsFromInitialize(WithCommands).ShouldBe(
        [
            new ClaudeCodeCommand("fleet-walkthrough", "Walk the user through a change", BuiltIn: false),
            new ClaudeCodeCommand("code-review", "Review the current diff", BuiltIn: true),
            new ClaudeCodeCommand("clear", "Start a new session with empty context", BuiltIn: true),
            new ClaudeCodeCommand("tidy", "A project command", BuiltIn: false),
        ]);
        ClaudeCodeCatalog.CommandsFromInitialize(Initialize).ShouldBeEmpty();
    }

    [Fact]
    public void The_composer_offers_Fleets_skills_and_the_users_commands_but_not_Claude_Codes_own()
    {
        // Claude Code's own /code-review would sit beside /fleet-code-review; typed in full, it still runs.
        ClaudeCodeHarnessSession.ComposerCommands(ClaudeCodeCatalog.CommandsFromInitialize(WithCommands)).Select(c => c.Name)
            .ShouldBe(["fleet-walkthrough", "tidy"]);
    }

    [Fact]
    public async Task One_answer_per_folder_and_set_of_built_in_skills_gives_both_the_models_and_the_commands()
    {
        var asked = new List<(string Directory, string? Skills)>();
        var catalog = new ClaudeCodeCatalog(new ClaudeCodeOptions(), NullLoggerFactory.Instance)
        {
            Ask = (directory, skills, _) => { asked.Add((directory, skills)); return Task.FromResult<JsonElement?>(WithCommands); },
        };
        var one = new ClaudeCodeSkillsFolder("/fleet/claude-code/skills/me", "fleet-walkthrough");

        await catalog.GetProvidersAsync("/work", CancellationToken.None, one);
        (await catalog.GetCommandsAsync("/work", one, CancellationToken.None)).Count.ShouldBe(4);
        await catalog.GetCommandsAsync("/work", skills: null, CancellationToken.None);
        // The owner's skills stay in one folder; turning another skill on changes what's in it.
        await catalog.GetCommandsAsync("/work", one with { Skills = "fleet-debug,fleet-walkthrough" }, CancellationToken.None);

        asked.ShouldBe([("/work", "/fleet/claude-code/skills/me"), ("/work", null), ("/work", "/fleet/claude-code/skills/me")]);
    }

    [Fact]
    public async Task When_Claude_Code_cant_say_the_composer_offers_no_commands()
    {
        var catalog = new ClaudeCodeCatalog(new ClaudeCodeOptions(), NullLoggerFactory.Instance)
        {
            Ask = (_, _, _) => throw new IOException("claude not found"),
        };

        (await catalog.GetCommandsAsync("/work", null, CancellationToken.None)).ShouldBeEmpty();
    }
}
