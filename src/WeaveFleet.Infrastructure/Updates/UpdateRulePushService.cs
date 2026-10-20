using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Updates;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Users;

namespace WeaveFleet.Infrastructure.Updates;

/// <summary>
/// On the home Fleet, while the Updates switch is on: works out the rule each machine should follow (its own override,
/// else the default the user chose, else its kind's: a node updates when idle, a Fleet asks) and gives it to the ones
/// that follow another. Looks again every half minute and when a machine is added, so a machine that reports a
/// different rule (it was reinstalled, or someone changed it there) gets the right one back. A node keeps its rule on
/// its own disk, so it acts on it even when this Fleet is off.
/// </summary>
public sealed partial class UpdateRulePushService(
    RemoteMachineService machines,
    IUpdateNodes nodes,
    UpdateRuleStore rules,
    MachineIdentityStore identity,
    FleetOptions options,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<UpdateRulePushService> logger) : BackgroundService
{
    /// <summary>Settings → Features: the switch for the whole feature.</summary>
    public const string SwitchKey = "FleetUpdates";

    /// <summary>The rule the user chose for every machine, unless one has its own.</summary>
    public const string DefaultKey = "FleetUpdateRule";

    /// <summary>A JSON object: machine id (<c>home</c> for this one) to its own rule.</summary>
    public const string OverridesKey = "FleetUpdateRules";

    public const string HomeKey = "home";

    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    /// <summary>The rule a machine follows: its override, the default, or what suits its kind.</summary>
    public static string Effective(string? overridden, string? chosenDefault, bool webApp)
        => UpdateRules.IsKnown(overridden) ? overridden!
            : UpdateRules.IsKnown(chosenDefault) ? chosenDefault!
            : webApp ? UpdateRules.Ask : UpdateRules.Idle;

    private readonly SemaphoreSlim _once = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Auth.Enabled || !options.Auth.TokenAuthEnabled)
            return;

        void Nudge(string machineId) => _ = SyncAsync(stoppingToken);
        machines.Changed += Nudge;
        try
        {
            using var timer = new PeriodicTimer(Every, time);
            do
            {
                await SyncAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            machines.Changed -= Nudge;
        }
    }

    /// <summary>One pass over this machine and the others. Returns how many rules it gave out.</summary>
    public async Task<int> SyncAsync(CancellationToken ct)
    {
        await _once.WaitAsync(ct);
        try
        {
            var (on, chosenDefault, overrides) = await ReadSettingsAsync();
            if (!on)
                return 0;

            var pushed = 0;
            var own = Effective(overrides.GetValueOrDefault(HomeKey), chosenDefault, options.ServeUi);
            if (rules.Rule != own)
            {
                rules.SetRule(own);
                pushed++;
            }

            var self = identity.Get().Id;
            foreach (var machine in await machines.ListAsync())
            {
                if (machine.Id == self)
                    continue;
                var info = await nodes.ReadAsync(machine, ct);
                if (info is null)
                    continue;

                var wanted = Effective(overrides.GetValueOrDefault(machine.Id), chosenDefault, info.WebApp);
                if (info.Rule == wanted)
                    continue;
                if (await nodes.PushRuleAsync(machine, wanted, ct))
                {
                    LogPushed(machine.Name, wanted);
                    pushed++;
                }
            }

            return pushed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSyncFailed(ex);
            return 0;
        }
        finally
        {
            _once.Release();
        }
    }

    private async Task<(bool On, string? Default, Dictionary<string, string> Overrides)> ReadSettingsAsync()
    {
        using var background = BackgroundUserContext.BeginScope("local-user");
        using var scope = scopes.CreateScope();
        var preferences = await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().GetAllAsync();
        var overrides = new Dictionary<string, string>();
        if (preferences.TryGetValue(OverridesKey, out var json))
        {
            try
            {
                overrides = JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.DictionaryStringString) ?? overrides;
            }
            catch (JsonException)
            {
                // A damaged value is no override.
            }
        }

        return (preferences.GetValueOrDefault(SwitchKey) == "true", preferences.GetValueOrDefault(DefaultKey), overrides);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Gave {Machine} the rule {Rule} for its updates.")]
    private partial void LogPushed(string machine, string rule);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't give the machines their update rules.")]
    private partial void LogSyncFailed(Exception ex);
}
