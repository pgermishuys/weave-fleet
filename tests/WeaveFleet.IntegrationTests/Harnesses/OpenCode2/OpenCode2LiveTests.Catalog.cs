using System.Text.Json;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// The live catalog: V2 rebuilds a folder's agents, models and commands whenever a file it watches changes, and Fleet
/// tells the owner's browsers so an open composer or slash-command list asks again.
/// </summary>
public sealed partial class OpenCode2LiveTests
{
    /// <summary>Longer than the server's settle time after a folder loads, so what follows is a change, not loading.</summary>
    private static readonly TimeSpan PastLoading = TimeSpan.FromSeconds(4);

    [OpenCode2Fact]
    public async Task An_agent_file_added_to_a_folder_is_told_once_and_the_catalog_lists_it()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var folder = fleet.NewFolder("catalog-agent-file");
        var changes = fleet.WatchTopics(["sessions"], cts.Token);

        (await CatalogAsync(folder, HarnessProfileService.NoProfile, cts.Token)).Agents.ShouldNotContain(a => a.Name == "hot");
        var id = await CreateSessionAsync(folder, "Live catalog", HarnessProfileService.NoProfile, cts.Token);
        var session = await fleet.HarnessSessionAsync(id, cts.Token);
        await Task.Delay(PastLoading, cts.Token);

        // Loading the folder and starting a session there aren't changes.
        CatalogChanges(changes, folder).ShouldBeEmpty();

        WriteAgent(folder, "hot");
        await WaitForAsync(changes, () => CatalogChanges(changes, folder).Count > 0, cts.Token);

        // V2 sends two or three rounds of events for one file; Fleet tells once.
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
        var change = CatalogChanges(changes, folder).ShouldHaveSingleItem();
        change.GetProperty("harnessType").GetString().ShouldBe(OpenCode2HarnessSession.Type);
        Strings(change, "profileIds").ShouldBe([HarnessProfileService.NoProfile]);
        Strings(change, "sessionIds").ShouldBe([id]);

        (await CatalogAsync(folder, HarnessProfileService.NoProfile, cts.Token)).Agents.ShouldContain(a => a.Name == "hot");
        (await session.GetAgentsAsync(cts.Token)).ShouldContain(a => a.Name == "hot");
    }

    [OpenCode2Fact]
    public async Task Each_server_that_loaded_a_folder_tells_its_own_profile()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var profile = await CreateProfileAsync("Live catalog", ProfileContent("catalog-model"), cts.Token);
        var folder = fleet.NewFolder("catalog-profiles");
        var changes = fleet.WatchTopics(["sessions"], cts.Token);

        await CatalogAsync(folder, HarnessProfileService.NoProfile, cts.Token);
        await CatalogAsync(folder, profile.Id, cts.Token);
        await Task.Delay(PastLoading, cts.Token);
        CatalogChanges(changes, folder).ShouldBeEmpty();

        WriteAgent(folder, "shared");
        await WaitForAsync(changes, () => CatalogChanges(changes, folder).Count >= 2, cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);

        CatalogChanges(changes, folder).Select(c => string.Join(",", Strings(c, "profileIds"))).Order()
            .ShouldBe(new[] { HarnessProfileService.NoProfile, profile.Id }.Order());
        (await CatalogAsync(folder, profile.Id, cts.Token)).Agents.ShouldContain(a => a.Name == "shared");
    }

    private static void WriteAgent(string folder, string name)
    {
        var agents = Path.Combine(folder, ".opencode", "agents");
        Directory.CreateDirectory(agents);
        File.WriteAllText(Path.Combine(agents, $"{name}.md"), $"---\ndescription: Added while Fleet ran\nmode: primary\n---\nThe {name} agent.\n");
    }

    /// <summary>The catalog changes Fleet told the owner about for <paramref name="folder"/>.</summary>
    private static List<JsonElement> CatalogChanges(LiveEvents events, string folder)
        => events.All
            .Where(e => e.Type == HarnessCatalogChanges.EventType
                && e.Payload.GetProperty("directory").GetString() == Path.TrimEndingDirectorySeparator(folder))
            .Select(e => e.Payload)
            .ToList();

    private static List<string?> Strings(JsonElement payload, string property)
        => payload.GetProperty(property).EnumerateArray().Select(e => e.GetString()).ToList();
}
