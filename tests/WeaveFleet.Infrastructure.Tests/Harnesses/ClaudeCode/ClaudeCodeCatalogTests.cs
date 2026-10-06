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
            Ask = (_, _) => { asked++; return Task.FromResult<JsonElement?>(Initialize); },
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
            Ask = (_, _) => ++asked == 1 ? throw new IOException("claude not found") : Task.FromResult<JsonElement?>(Initialize),
        };

        (await catalog.GetProvidersAsync("/work", CancellationToken.None)).ShouldBe(ClaudeCodeCatalog.Fallback);
        (await catalog.GetProvidersAsync("/work", CancellationToken.None))[0].Models.Count.ShouldBe(5);
    }
}
