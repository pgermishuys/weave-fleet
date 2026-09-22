extern alias FakeLlm;

using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// Profiles on OpenCode 2, live. A profile here brings a provider of its own on the same scripted model, whose model id
/// names the profile, so the model a request asks for says which config the turn ran on.
/// </summary>
public sealed partial class OpenCode2LiveTests
{
    [OpenCode2Fact]
    public async Task Two_sessions_in_one_folder_are_answered_by_their_own_profile_and_by_none()
    {
        const string prompt = "Who answers? (profile, same folder)";
        fleet.Answer(request => LlmRequest.Starts(request, prompt) ? new ScriptedLlmResponse { Text = $"Answered by {LlmRequest.Model(request)}." } : null);

        using var cts = new CancellationTokenSource(Timeout);
        var profile = await CreateProfileAsync("Same folder", ProfileContent("same-folder-model"), cts.Token);
        var folder = fleet.NewFolder("profile-same-folder");

        // The composer's catalog follows the profile: its model is the default and its agent is offered.
        var plainCatalog = await CatalogAsync(folder, HarnessProfileService.NoProfile, cts.Token);
        var profileCatalog = await CatalogAsync(folder, profile.Id, cts.Token);
        plainCatalog.DefaultModelId.ShouldBe("fake-model");
        plainCatalog.Agents.ShouldNotContain(a => a.Name == "profiled");
        profileCatalog.DefaultModelId.ShouldBe("same-folder-model");
        profileCatalog.DefaultModelProviderId.ShouldBe("prof");
        profileCatalog.Agents.ShouldContain(a => a.Name == "profiled");

        var plain = await CreateSessionAsync(folder, "No profile", HarnessProfileService.NoProfile, cts.Token);
        var profiled = await CreateSessionAsync(folder, "On a profile", profile.Id, cts.Token);
        var events = fleet.Watch(cts.Token, plain, profiled);

        await PromptAsync(plain, prompt, options: null, cts.Token);
        await PromptAsync(profiled, prompt, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(plain).Any(e => e.Type == "session.idle") && events.For(profiled).Any(e => e.Type == "session.idle"), cts.Token);

        LatestParts<TextMessageEventPart>(events, plain).Last().Text.ShouldBe("Answered by fake-model.");
        LatestParts<TextMessageEventPart>(events, profiled).Last().Text.ShouldBe("Answered by same-folder-model.");
        var plainServer = ((OpenCode2HarnessSession)await fleet.HarnessSessionAsync(plain, cts.Token)).ProcessId;
        var profileServer = ((OpenCode2HarnessSession)await fleet.HarnessSessionAsync(profiled, cts.Token)).ProcessId;
        profileServer.ShouldNotBe(plainServer);
    }

    [OpenCode2Fact]
    public async Task A_profiled_sessions_subagent_and_its_Fleet_child_run_on_the_parents_server_after_the_profile_changed()
    {
        const string warmup = "Start up. (profile subagent)";
        const string prompt = "Hand it to a helper. (profile subagent)";
        const string childPrompt = "Say which model you are. (profile child)";
        fleet.Answer(request =>
        {
            if (LlmRequest.Starts(request, childPrompt))
                return new ScriptedLlmResponse { Text = $"Child answered by {LlmRequest.Model(request)}." };
            if (LlmRequest.Starts(request, warmup))
                return new ScriptedLlmResponse { Text = "Ready." };
            if (LlmRequest.Starts(request, prompt))
                return ToolCall("call_profile_sub", "subagent", new { description = "Name the model", prompt = childPrompt, agent = "general", subagent_type = "general" });
            if (LlmRequest.Continues(request, prompt))
                return new ScriptedLlmResponse { Text = "The helper answered." };
            return null;
        });

        using var cts = new CancellationTokenSource(Timeout);
        var profile = await CreateProfileAsync("Delegating", ProfileContent("delegating-model"), cts.Token);
        var id = await CreateSessionAsync(fleet.NewFolder("profile-subagent"), "Profile subagent", profile.Id, cts.Token);
        var events = fleet.Watch(cts.Token, id);
        await PromptAsync(id, warmup, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);
        var parentServer = ((OpenCode2HarnessSession)await fleet.HarnessSessionAsync(id, cts.Token)).ProcessId.ShouldNotBeNull();

        // Edited while the parent runs: the child is prepared with the edit, which is another server's.
        var edited = await WithProfilesAsync(p => p.UpdateAsync(OpenCode2HarnessSession.Type, profile.Id, profile.Name,
            ProfileContent("delegating-model", """, "default_agent": "build" """), cts.Token));
        edited.IsSuccess.ShouldBeTrue(edited.IsFailure ? edited.Error.Description : null);

        await PromptAsync(id, prompt, options: null, cts.Token);
        var childId = await WaitForAsync(events, async () =>
            (await Delegations(id)).FirstOrDefault(d => d.ParentToolCallId == "call_profile_sub")?.ChildSessionId, cts.Token);
        await WaitForAsync(events, () => events.For(id).Count(e => e.Type == "session.idle") >= 2, cts.Token);
        await WaitForAsync(events, async () => (await Delegations(id)).Single(d => d.ParentToolCallId == "call_profile_sub").Status == "completed", cts.Token);

        // The subagent ran on the parent's profile, and its Fleet session listens on the parent's server.
        LlmRequest.LastToolText(fleet.Llm.Queue.Requests.Single(r => LlmRequest.Continues(r, prompt))).ShouldNotBeNull()
            .ShouldContain("Child answered by delegating-model.");
        var child = (OpenCode2HarnessSession)await fleet.HarnessSessionAsync(childId, cts.Token);
        child.ProcessId.ShouldBe(parentServer);
        LatestParts<TextMessageEventPart>(events, id).Last().Text.ShouldBe("The helper answered.");
    }

    [OpenCode2Fact]
    public async Task A_broken_profile_is_refused_with_what_OpenCode_2_would_leave_out_and_its_check_server_stops()
    {
        using var cts = new CancellationTokenSource(Timeout);
        const string broken = """
            {
              "model": 5,
              "nonsense": true,
              "plugin": ["/does/not/exist"]
            }
            """;

        var check = await WithProfilesAsync(p => p.CheckAsync(OpenCode2HarnessSession.Type, broken, cts.Token));

        check.IsSuccess.ShouldBeTrue(check.IsFailure ? check.Error.Description : null);
        check.Value.Ok.ShouldBeFalse();
        check.Value.Error.ShouldBe("OpenCode 2 would leave out part of this profile.");
        check.Value.Details.ShouldNotBeNull().ShouldBe(
        [
            "model: OpenCode 2 skipped this value because it isn't valid.",
            "nonsense: OpenCode 2 doesn't know this setting and ignores it.",
            "plugin /does/not/exist: OpenCode 2 couldn't load it (ENOENT: no such file or directory, stat '/does/not/exist').",
        ]);

        // Saving it is refused with the same reasons.
        var saved = await WithProfilesAsync(p => p.CreateAsync(OpenCode2HarnessSession.Type, "Broken", broken, cts.Token));
        saved.IsFailure.ShouldBeTrue();
        saved.Error.Description.ShouldContain("nonsense: OpenCode 2 doesn't know this setting");

        // Nothing is left running on it.
        if (OperatingSystem.IsLinux())
        {
            var data = Path.GetDirectoryName(Path.GetFullPath(fleet.Services.GetRequiredService<FleetOptions>().DatabasePath))!;
            var path = OpenCode2Profiles.Write(data, broken).ConfigPath;
            ProcessesWith(OpenCode2Profiles.EnvironmentVariable, path).ShouldBeEmpty();
        }
    }

    [OpenCode2Fact]
    public async Task An_idle_profile_server_stops_and_the_next_prompt_starts_it_again()
    {
        const string first = "Hello. (idle profile 1)";
        const string second = "Still there? (idle profile 2)";
        fleet.Answer(request => LlmRequest.Starts(request, first) || LlmRequest.Starts(request, second)
            ? new ScriptedLlmResponse { Text = $"Answered by {LlmRequest.Model(request)}." }
            : null);

        using var cts = new CancellationTokenSource(Timeout);
        var profile = await CreateProfileAsync("Idle", ProfileContent("idle-model"), cts.Token);
        var id = await CreateSessionAsync(fleet.NewFolder("profile-idle"), "Idle profile", profile.Id, cts.Token);
        var events = fleet.Watch(cts.Token, id);
        await PromptAsync(id, first, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);
        var harness = (OpenCode2HarnessSession)await fleet.HarnessSessionAsync(id, cts.Token);
        var server = harness.ProcessId.ShouldNotBeNull();

        // Not yet: it was used a moment ago.
        (await fleet.Runtime.StopIdleServersAsync(DateTimeOffset.UtcNow)).ShouldBe(0);

        (await fleet.Runtime.StopIdleServersAsync(DateTimeOffset.UtcNow.AddHours(1))).ShouldBeGreaterThanOrEqualTo(1);
        (await HasExitedAsync(server, TimeSpan.FromSeconds(10))).ShouldBeTrue($"Profile server {server} is still running.");

        await PromptAsync(id, second, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Count(e => e.Type == "session.idle") >= 2, cts.Token);
        LatestParts<TextMessageEventPart>(events, id).Last().Text.ShouldBe("Answered by idle-model.");
        harness.ProcessId.ShouldNotBeNull().ShouldNotBe(server);
    }

    /// <summary>A profile whose provider (<c>prof</c>) is the scripted model under the model id <paramref name="model"/>, with an agent of its own.</summary>
    private string ProfileContent(string model, string extra = "") => $$"""
        {
          // The scripted model again, as a provider of the profile's own.
          "model": "prof/{{model}}",
          "provider": {
            "prof": {
              "npm": "@ai-sdk/openai-compatible",
              "options": { "baseURL": "{{fleet.Llm.BaseUrl.ToString().TrimEnd('/')}}/v1", "apiKey": "profile-key" },
              "models": { "{{model}}": { "name": "{{model}}", "tool_call": true } }
            }
          },
          "agent": { "profiled": { "description": "Only on the profile", "mode": "primary", "prompt": "You are the profile's agent." } }{{extra}}
        }
        """;

    private async Task<HarnessProfileView> CreateProfileAsync(string name, string content, CancellationToken ct)
    {
        var created = await WithProfilesAsync(p => p.CreateAsync(OpenCode2HarnessSession.Type, name, content, ct));
        created.IsSuccess.ShouldBeTrue(created.IsFailure ? created.Error.Description : null);
        return created.Value;
    }

    private async Task<string> CreateSessionAsync(string folder, string title, string profileId, CancellationToken ct)
    {
        var created = await fleet.WithOrchestratorAsync(o => o.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = folder,
            Title = title,
            HarnessType = OpenCode2HarnessSession.Type,
            HarnessProfileId = profileId,
        }, ct));
        created.IsSuccess.ShouldBeTrue(created.IsFailure ? created.Error.Description : null);
        return created.Value.Session.Id;
    }

    private async Task<WeaveFleet.Domain.Harnesses.HarnessCatalog> CatalogAsync(string folder, string profileId, CancellationToken ct)
    {
        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = fleet.Services.CreateScope();
        var catalog = await scope.ServiceProvider.GetRequiredService<HarnessCatalogService>()
            .GetCatalogAsync(OpenCode2HarnessSession.Type, folder, ct, profileId);
        catalog.IsSuccess.ShouldBeTrue(catalog.IsFailure ? catalog.Error.Description : null);
        return catalog.Value.ShouldNotBeNull();
    }

    private async Task<T> WithProfilesAsync<T>(Func<HarnessProfileService, Task<T>> call)
    {
        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = fleet.Services.CreateScope();
        return await call(scope.ServiceProvider.GetRequiredService<HarnessProfileService>());
    }

    /// <summary>The processes of this user whose environment has <paramref name="name"/>=<paramref name="value"/> (Linux).</summary>
    private static List<int> ProcessesWith(string name, string value)
    {
        var wanted = $"{name}={value}";
        var found = new List<int>();
        foreach (var folder in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(folder), out var pid))
                continue;
            try
            {
                if (File.ReadAllText(Path.Combine(folder, "environ")).Split('\0').Contains(wanted))
                    found.Add(pid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another user's process, or gone.
            }
        }

        return found;
    }
}
