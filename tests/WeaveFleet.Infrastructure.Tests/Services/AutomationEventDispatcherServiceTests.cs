using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Tests.Services;

public sealed class AutomationEventDispatcherServiceTests
{
    [Fact]
    public async Task ProcessEvent_WhenDuplicateEvent_SkipsExecution()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<AutomationEventNotification>();
        var automationRepo = new FakeAutomationRepository();
        var ledgerRepo = new FakeLedgerRepository();
        var executionService = new FakeAutomationExecutionService();

        automationRepo.Seed(new Automation
        {
            Id = "auto-1",
            Name = "Test Automation",
            TriggerType = "event",
            TriggerConfig = """{"eventType":"session.started"}""",
            IsEnabled = true,
            UserId = "user-1",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        // Mark event as already processed
        await ledgerRepo.RecordAsync("auto-1", "evt-duplicate");

        var scopeFactory = BuildScopeFactory(automationRepo, ledgerRepo, executionService);
        var sut = new AutomationEventDispatcherService(channel, scopeFactory, NullLogger<AutomationEventDispatcherService>.Instance);

        using var cts = new CancellationTokenSource();
        var executeTask = StartBackgroundService(sut, cts.Token);
        await Task.Delay(50);

        // Act
        await channel.Writer.WriteAsync(new AutomationEventNotification(
            EventType: "session.started",
            EventId: "evt-duplicate",
            SessionId: null,
            SessionSourceReference: null
        ));

        // Wait for processing
        await Task.Delay(500);

        // Assert
        executionService.ExecutedAutomations.ShouldBeEmpty();

        // Cleanup
        await cts.CancelAsync();
        try { await executeTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ProcessEvent_WhenAutomationSourcedSession_SkipsFeedbackLoop()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<AutomationEventNotification>();
        var automationRepo = new FakeAutomationRepository();
        var ledgerRepo = new FakeLedgerRepository();
        var executionService = new FakeAutomationExecutionService();

        automationRepo.Seed(new Automation
        {
            Id = "auto-1",
            Name = "Test Automation",
            TriggerType = "event",
            TriggerConfig = """{"eventType":"message.created"}""",
            IsEnabled = true,
            UserId = "user-1",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var scopeFactory = BuildScopeFactory(automationRepo, ledgerRepo, executionService);
        var sut = new AutomationEventDispatcherService(channel, scopeFactory, NullLogger<AutomationEventDispatcherService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var executeTask = StartBackgroundService(sut, cts.Token);

        // Act - event from automation-sourced session
        await channel.Writer.WriteAsync(new AutomationEventNotification(
            EventType: "message.created",
            EventId: "evt-loop",
            SessionId: null,
            SessionSourceReference: "automation:auto-1"
        ));

        // Wait for processing
        await Task.Delay(500);

        // Assert
        executionService.ExecutedAutomations.ShouldBeEmpty();
        ledgerRepo.RecordedEvents.ShouldBeEmpty(); // Should not even record in ledger

        // Cleanup
        await cts.CancelAsync();
        try { await executeTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ProcessEvent_WhenAutomationSourcedSessionCaseInsensitive_SkipsFeedbackLoop()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<AutomationEventNotification>();
        var automationRepo = new FakeAutomationRepository();
        var ledgerRepo = new FakeLedgerRepository();
        var executionService = new FakeAutomationExecutionService();

        automationRepo.Seed(new Automation
        {
            Id = "auto-1",
            Name = "Test Automation",
            TriggerType = "event",
            TriggerConfig = """{"eventType":"message.created"}""",
            IsEnabled = true,
            UserId = "user-1",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var scopeFactory = BuildScopeFactory(automationRepo, ledgerRepo, executionService);
        var sut = new AutomationEventDispatcherService(channel, scopeFactory, NullLogger<AutomationEventDispatcherService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var executeTask = StartBackgroundService(sut, cts.Token);

        // Act - event with uppercase "AUTOMATION:" prefix
        await channel.Writer.WriteAsync(new AutomationEventNotification(
            EventType: "message.created",
            EventId: "evt-loop-upper",
            SessionId: null,
            SessionSourceReference: "AUTOMATION:auto-1"
        ));

        // Wait for processing
        await Task.Delay(500);

        // Assert
        executionService.ExecutedAutomations.ShouldBeEmpty();

        // Cleanup
        await cts.CancelAsync();
        try { await executeTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ProcessEvent_WhenNoMatchingAutomations_DoesNotExecute()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<AutomationEventNotification>();
        var automationRepo = new FakeAutomationRepository();
        var ledgerRepo = new FakeLedgerRepository();
        var executionService = new FakeAutomationExecutionService();

        // No automations seeded

        var scopeFactory = BuildScopeFactory(automationRepo, ledgerRepo, executionService);
        var sut = new AutomationEventDispatcherService(channel, scopeFactory, NullLogger<AutomationEventDispatcherService>.Instance);

        using var cts = new CancellationTokenSource();
        var executeTask = StartBackgroundService(sut, cts.Token);
        await Task.Delay(50);

        // Act
        await channel.Writer.WriteAsync(new AutomationEventNotification(
            EventType: "session.started",
            EventId: "evt-no-match",
            SessionId: null,
            SessionSourceReference: null
        ));

        // Wait for processing
        await Task.Delay(500);

        // Assert
        executionService.ExecutedAutomations.ShouldBeEmpty();
        ledgerRepo.RecordedEvents.ShouldBeEmpty();

        // Cleanup
        await cts.CancelAsync();
        try { await executeTask; } catch (OperationCanceledException) { }
    }

    // ── Test Helpers ─────────────────────────────────────────────────────────

    private static IServiceScopeFactory BuildScopeFactory(
        IAutomationRepository automationRepo,
        IAutomationEventLedgerRepository ledgerRepo,
        FakeAutomationExecutionService executionService,
        ISessionRepository? sessionRepo = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(automationRepo);
        services.AddSingleton(ledgerRepo);
        services.AddSingleton(executionService);
        services.AddSingleton<ISessionRepository>(sessionRepo ?? new FakeSessionRepository());
        services.AddSingleton<EventTriggerMatcher>();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>
    /// Starts the protected ExecuteAsync method of a BackgroundService using reflection.
    /// </summary>
    private static Task StartBackgroundService(Microsoft.Extensions.Hosting.BackgroundService service, CancellationToken ct)
    {
        var executeMethod = typeof(Microsoft.Extensions.Hosting.BackgroundService)
            .GetMethod("ExecuteAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        
        if (executeMethod is null)
            throw new InvalidOperationException("Could not find ExecuteAsync method on BackgroundService");
        
        return (Task)executeMethod.Invoke(service, [ct])!;
    }
}

// ── Fake Implementations ─────────────────────────────────────────────────────

internal sealed class FakeAutomationRepository : IAutomationRepository
{
    private readonly Dictionary<string, Automation> _store = new();

    public void Seed(Automation automation) => _store[automation.Id] = automation;

    public void Seed(params Automation[] automations)
    {
        foreach (var a in automations)
            Seed(a);
    }

    public Task InsertAsync(Automation automation)
    {
        _store[automation.Id] = automation;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Automation automation)
    {
        _store[automation.Id] = automation;
        return Task.CompletedTask;
    }

    public Task<Automation?> GetByIdAsync(string id)
        => Task.FromResult(_store.GetValueOrDefault(id));

    public Task<IReadOnlyList<Automation>> ListAsync(string? workspaceId = null)
    {
        var results = _store.Values.Where(a => !a.IsDeleted).ToList();
        if (workspaceId is not null)
            results = results.Where(a => a.WorkspaceId == workspaceId).ToList();
        return Task.FromResult<IReadOnlyList<Automation>>(results);
    }

    public Task<IReadOnlyList<Automation>> ListEnabledByTriggerTypeAsync(string triggerType)
    {
        var results = _store.Values
            .Where(a => a.IsEnabled && !a.IsDeleted && a.TriggerType == triggerType)
            .ToList();
        return Task.FromResult<IReadOnlyList<Automation>>(results);
    }

    public Task DeleteAsync(string id)
    {
        if (_store.TryGetValue(id, out var automation))
            automation.IsDeleted = true;
        return Task.CompletedTask;
    }

    public Task SetEnabledAsync(string id, bool enabled)
    {
        if (_store.TryGetValue(id, out var automation))
            automation.IsEnabled = enabled;
        return Task.CompletedTask;
    }
}

internal sealed class FakeLedgerRepository : IAutomationEventLedgerRepository
{
    private readonly HashSet<(string AutomationId, string EventId)> _processed = new();

    public List<(string AutomationId, string EventId)> RecordedEvents { get; } = [];

    public Task<bool> IsProcessedAsync(string automationId, string sourceEventId)
        => Task.FromResult(_processed.Contains((automationId, sourceEventId)));

    public Task RecordAsync(string automationId, string sourceEventId)
    {
        _processed.Add((automationId, sourceEventId));
        RecordedEvents.Add((automationId, sourceEventId));
        return Task.CompletedTask;
    }
}

internal sealed class FakeAutomationExecutionService
{
    public List<(string AutomationId, string EventType, string? EventSummary)> ExecutedAutomations { get; } = [];

    public Task ExecuteAsync(Automation automation, string eventType, string? eventSummary, CancellationToken ct)
    {
        ExecutedAutomations.Add((automation.Id, eventType, eventSummary));
        return Task.CompletedTask;
    }
}

internal sealed class FakeSessionRepository : ISessionRepository
{
    private readonly Dictionary<string, Domain.Entities.Session> _store = new();

    public List<string> GetByIdAsyncCalls { get; } = [];

    public void Seed(Domain.Entities.Session session) => _store[session.Id] = session;

    public Task<Domain.Entities.Session?> GetByIdAsync(string id)
    {
        GetByIdAsyncCalls.Add(id);
        return Task.FromResult(_store.GetValueOrDefault(id));
    }

    public Task UpdateTagsAsync(string id, List<string> tags)
    {
        if (_store.TryGetValue(id, out var session))
            session.Tags = tags;
        return Task.CompletedTask;
    }

    // Not implemented - not needed for these tests
    public Task InsertAsync(Domain.Entities.Session session) => throw new NotImplementedException();
    public Task InsertAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction? transaction, Domain.Entities.Session session) => throw new NotImplementedException();
    public Task<Domain.Entities.Session?> GetByHarnessIdAsync(string harnessSessionId) => throw new NotImplementedException();
    public Task<IReadOnlyList<Domain.Entities.Session>> ListAsync(int limit = 100, int offset = 0, IReadOnlyList<string>? statuses = null, string? projectId = null) => throw new NotImplementedException();
    public Task<IReadOnlyList<Domain.Entities.Session>> ListAsync(int limit, int offset, IReadOnlyList<string>? statuses, string? projectId, IReadOnlyList<string>? retentionStatuses) => throw new NotImplementedException();
    public Task<IReadOnlyList<Domain.Entities.Session>> ListAsync(int limit, int offset, IReadOnlyList<string>? statuses, string? projectId, IReadOnlyList<string>? retentionStatuses, IReadOnlyList<string>? tags) => throw new NotImplementedException();
    public Task DeleteByProjectIdAsync(string projectId) => throw new NotImplementedException();
    public Task<int> CountAsync(IReadOnlyList<string>? statuses = null) => throw new NotImplementedException();
    public Task<int> CountAsync(IReadOnlyList<string>? statuses, IReadOnlyList<string>? retentionStatuses) => throw new NotImplementedException();
    public Task<(int Active, int Idle)> GetStatusCountsAsync() => throw new NotImplementedException();
    public Task<IReadOnlyList<Domain.Entities.Session>> ListActiveAsync() => throw new NotImplementedException();
    public Task<IReadOnlyList<Domain.Entities.Session>> ListActiveAsync(IReadOnlyList<string>? retentionStatuses) => throw new NotImplementedException();
    public Task UpdateStatusAsync(string id, string status, string? stoppedAt = null) => throw new NotImplementedException();
    public Task UpdateStatusAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction? transaction, string id, string status, string? stoppedAt) => throw new NotImplementedException();
    public Task ArchiveAsync(string id, string archivedAt) => throw new NotImplementedException();
    public Task ArchiveAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction? transaction, string id, string archivedAt) => throw new NotImplementedException();
    public Task UnarchiveAsync(string id) => throw new NotImplementedException();
    public Task UnarchiveAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction? transaction, string id) => throw new NotImplementedException();
    public Task<IReadOnlyList<Domain.Entities.Session>> GetForInstanceAsync(string instanceId) => throw new NotImplementedException();
    public Task<Domain.Entities.Session?> GetAnyForInstanceAsync(string instanceId) => throw new NotImplementedException();
    public Task<IReadOnlyList<Domain.Entities.Session>> GetNonTerminalForInstanceAsync(string instanceId) => throw new NotImplementedException();
    public Task UpdateTitleAsync(string id, string title) => throw new NotImplementedException();
    public Task UpdateForResumeAsync(string id, string instanceId) => throw new NotImplementedException();
    public Task UpdateResumeTokenAsync(string id, string resumeToken) => throw new NotImplementedException();
    public Task<IReadOnlyList<Domain.Entities.Session>> GetActiveChildrenAsync(string parentDbId) => throw new NotImplementedException();
    public Task<IReadOnlySet<string>> GetIdsWithActiveChildrenAsync() => throw new NotImplementedException();
    public Task<IReadOnlyDictionary<string, string>> GetActiveChildToParentMappingAsync() => throw new NotImplementedException();
    public Task<IReadOnlyList<Domain.Entities.Session>> GetForWorkspaceAsync(string workspaceId) => throw new NotImplementedException();
    public Task<IReadOnlyList<Domain.Entities.Session>> GetForWorkspaceAsync(string workspaceId, IReadOnlyList<string>? retentionStatuses) => throw new NotImplementedException();
    public Task<bool> DeleteAsync(string id) => throw new NotImplementedException();
    public Task<bool> DeleteAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction? transaction, string id) => throw new NotImplementedException();
    public Task<(int TotalTokens, double TotalCost)?> IncrementTokensAsync(string id, int tokens, double cost) => throw new NotImplementedException();
    public Task<(int TotalTokens, double TotalCost)> GetFleetTokenTotalsAsync() => throw new NotImplementedException();
    public Task<int> MarkAllNonTerminalStoppedAsync(string stoppedAt) => throw new NotImplementedException();
    public Task UpdateProjectAsync(string id, string? projectId) => throw new NotImplementedException();
    public Task UpdateSelectedModelAsync(string id, string providerId, string modelId) => throw new NotImplementedException();
}
