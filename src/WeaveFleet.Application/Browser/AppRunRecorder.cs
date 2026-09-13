using System.Globalization;
using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Browser;

/// <summary>
/// Stores each change to a run and tells the session's clients (<c>app.updated</c>), as the run's owner.
/// At startup it cleans up after the previous Fleet: processes a crash left running are killed, and every
/// run it was running is marked stopped. Fleet never starts them again on its own.
/// </summary>
public sealed class AppRunRecorder(
    IAppRunner apps,
    IAppRunRepository runs,
    IEventBroadcaster events,
    IBackgroundUserScope userScope)
{
    /// <summary>
    /// Records <paramref name="change"/>. Changes can arrive out of order, so what's stored and sent is the
    /// run as it is now; the reason is the change's.
    /// </summary>
    public async Task RecordAsync(AppRunChange change, CancellationToken ct = default)
    {
        var app = apps.Find(change.App.Id) ?? change.App;
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        using (userScope.Begin(app.UserId))
        {
            var existing = await runs.GetByIdAsync(app.SessionId, app.Id);
            var stored = await runs.UpsertAsync(new AppRun
            {
                Id = app.Id,
                SessionId = app.SessionId,
                UserId = app.UserId,
                Command = app.Command,
                Directory = app.Directory,
                Port = app.Port,
                Status = StatusText(app.Status),
                ExitCode = app.ExitCode,
                Url = app.Url,
                Pid = app.Pid,
                PidStartedAt = app.PidStartedAt?.ToString("O", CultureInfo.InvariantCulture),
                CreatedAt = existing?.CreatedAt ?? now,
                UpdatedAt = now,
            });

            // The session is gone (deleted while its apps stopped): nobody to tell.
            if (!stored)
                return;
        }

        var payload = new AppUpdatedPayload
        {
            SessionId = app.SessionId,
            AppId = app.Id,
            Command = app.Command,
            Status = StatusText(app.Status),
            Url = app.Url,
            Ports = app.Ports,
            ExitCode = app.ExitCode,
            Reason = change.Reason.ToString().ToLowerInvariant(),
        };
        await events.BroadcastAsync(
            $"session:{app.SessionId}",
            "app.updated",
            JsonSerializer.SerializeToElement(payload, ApplicationJsonContext.Default.AppUpdatedPayload),
            new AppUpdated { Payload = payload },
            app.UserId,
            ct);
    }

    /// <summary>
    /// Kills what a previous Fleet left running (the pid and its start time must both match, so a later
    /// process with a reused pid is left alone) and marks those runs stopped. Returns how many it killed.
    /// </summary>
    public async Task<int> CleanUpAfterPreviousFleetAsync()
    {
        var unfinished = await runs.ListUnfinishedForAllUsersAsync();
        var killed = 0;
        foreach (var run in unfinished)
        {
            if (run.Pid is { } pid
                && DateTimeOffset.TryParse(run.PidStartedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startedAt)
                && apps.KillLeftover(pid, startedAt))
            {
                killed++;
            }
        }

        await runs.MarkStoppedForAllUsersAsync([.. unfinished.Select(run => run.Id)], DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return killed;
    }

    public static string StatusText(AppRunStatus status) => status.ToString().ToLowerInvariant();
}
