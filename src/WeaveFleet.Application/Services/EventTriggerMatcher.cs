using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

public sealed partial class EventTriggerMatcher
{
    private readonly IAutomationRepository _repository;
    private readonly ILogger<EventTriggerMatcher> _logger;

    public EventTriggerMatcher(IAutomationRepository repository, ILogger<EventTriggerMatcher> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Automation>> FindMatchingAutomationsAsync(string eventType, CancellationToken ct = default)
    {
        var automations = await _repository.ListEnabledByTriggerTypeAsync("event");
        var matches = new List<Automation>();

        foreach (var automation in automations)
        {
            try
            {
                using var doc = JsonDocument.Parse(automation.TriggerConfig);
                if (doc.RootElement.TryGetProperty("eventType", out var eventTypeProp) &&
                    string.Equals(eventTypeProp.GetString(), eventType, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(automation);
                }
            }
            catch (JsonException ex)
            {
                LogMalformedTriggerConfig(ex, automation.Id);
            }
        }

        return matches;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Malformed trigger_config for automation {AutomationId}, skipping")]
    private partial void LogMalformedTriggerConfig(Exception ex, string automationId);
}
