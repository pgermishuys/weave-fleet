extern alias FakeLlm;

using System.Diagnostics;
using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Skills;
using WeaveFleet.Domain.Events;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// A shell call moved to the background keeps running after its turn ended, when V2 no longer counts its session as
/// active. Fleet stops or replaces a server only when nothing runs on it, so the command isn't killed and its notice
/// still arrives.
/// </summary>
public sealed partial class OpenCode2LiveTests
{
    [OpenCode2Fact]
    public async Task A_profile_server_stays_up_while_a_background_shell_runs_and_stops_once_it_has_finished()
    {
        const string prompt = "Start the long one. (idle background shell)";
        fleet.Answer(request =>
        {
            if (LlmRequest.Starts(request, prompt))
                return ToolCall("call_idle_bg", "shell", new { command = "sleep 8; echo idle-late-output", description = "A slow job", background = true });
            if (LlmRequest.Continues(request, prompt))
                return new ScriptedLlmResponse { Text = "It's running in the background." };
            return null;
        });

        using var cts = new CancellationTokenSource(Timeout);
        var profile = await CreateProfileAsync("Background idle", ProfileContent("background-idle-model"), cts.Token);
        var id = await CreateSessionAsync(fleet.NewFolder("profile-background-idle"), "Background idle", profile.Id, cts.Token);
        var events = fleet.Watch(cts.Token, id);
        await PromptAsync(id, prompt, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);
        var harness = (OpenCode2HarnessSession)await fleet.HarnessSessionAsync(id, cts.Token);
        var server = harness.ProcessId.ShouldNotBeNull();

        // The turn is over and the server has long been unused, but the shell still runs: the server stays.
        await fleet.Runtime.StopIdleServersAsync(DateTimeOffset.UtcNow.AddHours(1));
        IsRunning(server).ShouldBeTrue($"Profile server {server} was stopped while its background shell ran.");

        // So the command finishes, V2 posts its notice, and the session picks up (a turn of its own) on the same server.
        var notice = await WaitForAsync(events, () => Task.FromResult(LatestParts<TextMessageEventPart>(events, id)
            .FirstOrDefault(p => p.Text.StartsWith("<shell", StringComparison.Ordinal) && p.Text.Contains("idle-late-output", StringComparison.Ordinal))), cts.Token);
        notice.Text.ShouldContain("state=\"completed\"");
        harness.ProcessId.ShouldBe(server);

        // Once the turn the notice started is over too, nothing runs on it, and it stops.
        await WaitForAsync(events, async () =>
        {
            await fleet.Runtime.StopIdleServersAsync(DateTimeOffset.UtcNow.AddHours(1));
            return !IsRunning(server);
        }, cts.Token);
        events.For(id).Count(e => e.Type == "session.idle").ShouldBeGreaterThanOrEqualTo(2);
    }

    [OpenCode2Fact]
    public async Task A_server_whose_settings_changed_is_replaced_only_once_its_background_shell_has_finished()
    {
        const string prompt = "Start the long one. (settings background shell)";
        fleet.Answer(request =>
        {
            if (LlmRequest.Starts(request, prompt))
                return ToolCall("call_settings_bg", "shell", new { command = "sleep 8; echo settings-late-output", description = "A slow job", background = true });
            if (LlmRequest.Continues(request, prompt))
                return new ScriptedLlmResponse { Text = "It's running in the background." };
            return null;
        });

        using var cts = new CancellationTokenSource(Timeout);
        var folder = fleet.NewFolder("settings-background-shell");
        var id = await fleet.CreateSessionAsync(folder, "Settings background shell", cts.Token);
        var events = fleet.Watch(cts.Token, id);
        await PromptAsync(id, prompt, options: null, cts.Token);
        await WaitForAsync(events, () => events.For(id).Any(e => e.Type == "session.idle"), cts.Token);
        var harness = (OpenCode2HarnessSession)await fleet.HarnessSessionAsync(id, cts.Token);
        var server = harness.ProcessId.ShouldNotBeNull();

        var skill = (await WithBuiltInSkillsAsync(s => s.ListAsync())).First(s => !s.Enabled).Name;
        try
        {
            // A built-in skill switched on while the shell runs: the server started without it, but it isn't replaced yet.
            (await WithBuiltInSkillsAsync(s => s.SetEnabledAsync(skill, enabled: true))).IsSuccess.ShouldBeTrue();
            _ = await CatalogAsync(folder, HarnessProfileService.NoProfile, cts.Token);
            IsRunning(server).ShouldBeTrue($"OpenCode 2 server {server} was replaced while its background shell ran.");

            // So the command finishes, V2 posts its notice, and the session picks up (a turn of its own).
            await WaitForAsync(events, () => LatestParts<TextMessageEventPart>(events, id)
                .Any(p => p.Text.StartsWith("<shell", StringComparison.Ordinal) && p.Text.Contains("settings-late-output", StringComparison.Ordinal)), cts.Token);
            harness.ProcessId.ShouldBe(server);

            // Once it's finished (and the turn the notice started), the next request gets a server with the new settings.
            await WaitForAsync(events, async () =>
            {
                _ = await CatalogAsync(folder, HarnessProfileService.NoProfile, cts.Token);
                return !IsRunning(server);
            }, cts.Token);
            events.For(id).Count(e => e.Type == "session.idle").ShouldBeGreaterThanOrEqualTo(2);
        }
        finally
        {
            await WithBuiltInSkillsAsync(s => s.SetEnabledAsync(skill, enabled: false));
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task<T> WithBuiltInSkillsAsync<T>(Func<BuiltInSkillService, Task<T>> call)
    {
        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = fleet.Services.CreateScope();
        return await call(scope.ServiceProvider.GetRequiredService<BuiltInSkillService>());
    }
}
