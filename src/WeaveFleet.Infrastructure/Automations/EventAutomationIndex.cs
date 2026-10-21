using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Automations;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Automations;

/// <summary>
/// Singleton <see cref="IEventAutomationIndex"/>: loads the event types of the enabled event automations
/// once, then again only after <see cref="Invalidate"/>.
/// </summary>
public sealed class EventAutomationIndex(IServiceScopeFactory scopeFactory) : IEventAutomationIndex, IDisposable
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile HashSet<string>? _eventTypes;
    private long _version;

    public void Dispose() => _loadLock.Dispose();

    public void Invalidate()
    {
        Interlocked.Increment(ref _version);
        _eventTypes = null;
    }

    public bool MayMatch(string eventType)
    {
        var loaded = _eventTypes;
        return loaded is null || loaded.Contains(eventType);
    }

    public async Task<bool> AnyListensToAsync(string eventType, CancellationToken ct)
    {
        var loaded = _eventTypes;
        if (loaded is not null)
            return loaded.Contains(eventType);

        await _loadLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            loaded = _eventTypes;
            if (loaded is not null)
                return loaded.Contains(eventType);

            var versionBeforeLoad = Interlocked.Read(ref _version);
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAutomationRepository>();
            var automations = await repository.ListEnabledByTriggerTypeAsync("event").ConfigureAwait(false);

            loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var automation in automations)
            {
                try
                {
                    using var doc = JsonDocument.Parse(automation.TriggerConfig);
                    if (doc.RootElement.TryGetProperty("eventType", out var prop) && prop.GetString() is { } type)
                        loaded.Add(type);
                }
                catch (JsonException)
                {
                    // Malformed trigger config never matches; the matcher logs it when it runs.
                }
            }

            // Only keep the result if nothing changed while we were reading.
            if (Interlocked.Read(ref _version) == versionBeforeLoad)
                _eventTypes = loaded;
            return loaded.Contains(eventType);
        }
        finally
        {
            _loadLock.Release();
        }
    }
}
