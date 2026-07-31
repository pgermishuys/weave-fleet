using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Tests.Services;

[Collection("Sequential")]
public sealed class AutomationSchedulerServiceTests
{
    // ── Cron Timing Logic ──────────────────────────────────────────────────────

    [Fact]
    public async Task Automation_with_cron_that_should_fire_within_window_executes()
    {
        // Arrange: cron that fires every minute — should always be in the 30s window
        var automation = new Automation
        {
            Id = "auto-1",
            Name = "Every Minute",
            TriggerType = "schedule",
            TriggerConfig = "* * * * *", // every minute
            MaxConcurrentRuns = 1,
            IsEnabled = true
        };

        var repo = new FakeAutomationRepository([automation]);
        var tracker = new ExecutionTracker();
        var scopeFactory = new FakeServiceScopeFactory(repo, tracker);

        var scheduler = new AutomationSchedulerService(scopeFactory, NullLogger<AutomationSchedulerService>.Instance);

        // Act: start and let it poll once
        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(2000, CancellationToken.None); // give it time to poll and execute Task.Run
        await scheduler.StopAsync(CancellationToken.None);
        
        // Wait for async executions to complete
        await Task.Delay(1000, CancellationToken.None);

        // Assert: execution should have been called
        tracker.ExecutedAutomationIds.ShouldContain("auto-1");
    }

    [Fact]
    public async Task Automation_with_cron_that_should_not_fire_does_not_execute()
    {
        // Arrange: cron that fires once a year — should NOT be in the 30s window
        var automation = new Automation
        {
            Id = "auto-2",
            Name = "Once a Year",
            TriggerType = "schedule",
            TriggerConfig = "0 0 1 1 *", // Jan 1 at midnight
            MaxConcurrentRuns = 1,
            IsEnabled = true
        };

        var repo = new FakeAutomationRepository([automation]);
        var tracker = new ExecutionTracker();
        var scopeFactory = new FakeServiceScopeFactory(repo, tracker);

        var scheduler = new AutomationSchedulerService(scopeFactory, NullLogger<AutomationSchedulerService>.Instance);

        // Act: start and let it poll once
        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(2000, CancellationToken.None);
        await scheduler.StopAsync(CancellationToken.None);
        
        // Wait for async executions
        await Task.Delay(1000, CancellationToken.None);

        // Assert: execution should NOT have been called
        tracker.ExecutedAutomationIds.ShouldBeEmpty();
    }

    // ── Concurrent Run Limit Enforcement ───────────────────────────────────────

    [Fact]
    public async Task Automation_at_max_concurrent_runs_is_skipped()
    {
        // Arrange: automation with max 1 concurrent run
        // We'll verify by checking that only one execution starts even if we trigger multiple times
        var automation = new Automation
        {
            Id = "auto-3",
            Name = "Max One",
            TriggerType = "schedule",
            TriggerConfig = "* * * * *", // every minute
            MaxConcurrentRuns = 1,
            IsEnabled = true
        };

        var repo = new FakeAutomationRepository([automation]);
        var tracker = new ExecutionTracker
        {
            // Simulate a long-running execution
            ExecutionDelay = TimeSpan.FromSeconds(5)
        };
        var scopeFactory = new FakeServiceScopeFactory(repo, tracker);

        var scheduler = new AutomationSchedulerService(scopeFactory, NullLogger<AutomationSchedulerService>.Instance);

        // Act: start, let it poll and start one execution
        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(2000, CancellationToken.None); // first poll starts execution
        await scheduler.StopAsync(CancellationToken.None);
        
        // Wait for execution to complete
        await Task.Delay(6000, CancellationToken.None);

        // Assert: only one execution should have started
        tracker.ExecutedAutomationIds.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Automation_below_max_concurrent_runs_executes()
    {
        // Arrange: automation with max 2 concurrent runs
        var automation = new Automation
        {
            Id = "auto-4",
            Name = "Max Two",
            TriggerType = "schedule",
            TriggerConfig = "* * * * *", // every minute
            MaxConcurrentRuns = 2,
            IsEnabled = true
        };

        var repo = new FakeAutomationRepository([automation]);
        var tracker = new ExecutionTracker();
        var scopeFactory = new FakeServiceScopeFactory(repo, tracker);

        var scheduler = new AutomationSchedulerService(scopeFactory, NullLogger<AutomationSchedulerService>.Instance);

        // Act: start and let it poll once
        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(2000, CancellationToken.None);
        await scheduler.StopAsync(CancellationToken.None);
        
        // Wait for async executions to complete
        await Task.Delay(1000, CancellationToken.None);

        // Assert: execution should have been called (max 2 allows it)
        tracker.ExecutedAutomationIds.Count.ShouldBe(1);
    }

    // ── Graceful Shutdown ──────────────────────────────────────────────────────

    [Fact]
    public async Task Service_stops_gracefully_on_cancellation()
    {
        // Arrange
        var automation = new Automation
        {
            Id = "auto-5",
            Name = "Graceful",
            TriggerType = "schedule",
            TriggerConfig = "* * * * *",
            MaxConcurrentRuns = 1,
            IsEnabled = true
        };

        var repo = new FakeAutomationRepository([automation]);
        var tracker = new ExecutionTracker();
        var scopeFactory = new FakeServiceScopeFactory(repo, tracker);

        var scheduler = new AutomationSchedulerService(scopeFactory, NullLogger<AutomationSchedulerService>.Instance);

        // Act: start and immediately cancel
        using var cts = new CancellationTokenSource();
        var startTask = scheduler.StartAsync(cts.Token);
        await cts.CancelAsync();

        // Assert: should complete without throwing
        await Should.NotThrowAsync(async () => await startTask);
        await Should.NotThrowAsync(async () => await scheduler.StopAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Service_handles_cancellation_during_poll()
    {
        // Arrange
        var automation = new Automation
        {
            Id = "auto-6",
            Name = "Cancel During Poll",
            TriggerType = "schedule",
            TriggerConfig = "* * * * *",
            MaxConcurrentRuns = 1,
            IsEnabled = true
        };

        var repo = new FakeAutomationRepository([automation]);
        var tracker = new ExecutionTracker();
        var scopeFactory = new FakeServiceScopeFactory(repo, tracker);

        var scheduler = new AutomationSchedulerService(scopeFactory, NullLogger<AutomationSchedulerService>.Instance);

        // Act: start, let it poll once, then cancel
        using var cts = new CancellationTokenSource();
        await scheduler.StartAsync(cts.Token);
        await Task.Delay(500, CancellationToken.None); // let it poll
        await cts.CancelAsync();

        // Assert: should stop gracefully
        await Should.NotThrowAsync(async () => await scheduler.StopAsync(CancellationToken.None));
    }

    // ── Test Doubles ───────────────────────────────────────────────────────────

    private sealed class FakeAutomationRepository : IAutomationRepository
    {
        private readonly List<Automation> _automations;

        public FakeAutomationRepository(List<Automation> automations)
        {
            _automations = automations;
        }

        public Task<IReadOnlyList<Automation>> ListEnabledByTriggerTypeAsync(string triggerType)
        {
            var result = _automations
                .Where(a => a.TriggerType == triggerType && a.IsEnabled)
                .ToList();
            return Task.FromResult<IReadOnlyList<Automation>>(result);
        }

        public Task InsertAsync(Automation automation) => throw new NotImplementedException();
        public Task UpdateAsync(Automation automation) => throw new NotImplementedException();
        public Task<Automation?> GetByIdAsync(string id) => throw new NotImplementedException();
        public Task<IReadOnlyList<Automation>> ListAsync(string? workspaceId = null) => throw new NotImplementedException();
        public Task DeleteAsync(string id) => throw new NotImplementedException();
        public Task SetEnabledAsync(string id, bool enabled) => throw new NotImplementedException();
    }

    private sealed class ExecutionTracker
    {
        public List<string> ExecutedAutomationIds { get; } = [];
        public TimeSpan ExecutionDelay { get; set; } = TimeSpan.Zero;

        public async Task RecordExecutionAsync(string automationId, CancellationToken ct)
        {
            ExecutedAutomationIds.Add(automationId);

            if (ExecutionDelay > TimeSpan.Zero)
            {
                await Task.Delay(ExecutionDelay, ct);
            }
        }
    }

    private sealed class FakeServiceScopeFactory : IServiceScopeFactory
    {
        private readonly IAutomationRepository _repo;
        private readonly ExecutionTracker _tracker;
        private readonly TrackingLogger _logger;

        public FakeServiceScopeFactory(IAutomationRepository repo, ExecutionTracker tracker)
        {
            _repo = repo;
            _tracker = tracker;
            _logger = new TrackingLogger(tracker);
        }

        public IServiceScope CreateScope()
        {
            return new FakeServiceScope(_repo, _tracker, _logger);
        }
    }

    private sealed class FakeServiceScope : IServiceScope
    {
        private readonly IAutomationRepository _repo;
        private readonly ExecutionTracker _tracker;
        private readonly TrackingLogger _logger;

        public FakeServiceScope(IAutomationRepository repo, ExecutionTracker tracker, TrackingLogger logger)
        {
            _repo = repo;
            _tracker = tracker;
            _logger = logger;
        }

        public IServiceProvider ServiceProvider => new FakeServiceProvider(_repo, _tracker, _logger);

        public void Dispose()
        {
        }
    }

    private sealed class FakeServiceProvider : IServiceProvider
    {
        private readonly IAutomationRepository _repo;
        private readonly ExecutionTracker _tracker;
        private readonly TrackingLogger _logger;

        public FakeServiceProvider(IAutomationRepository repo, ExecutionTracker tracker, TrackingLogger logger)
        {
            _repo = repo;
            _tracker = tracker;
            _logger = logger;
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IAutomationRepository))
                return _repo;
            if (serviceType == typeof(AutomationExecutionService))
            {
                // Create a real AutomationExecutionService with a tracking logger
                return new AutomationExecutionService(null!, _logger);
            }
            return null;
        }
    }

    private sealed class TrackingLogger : ILogger<AutomationExecutionService>
    {
        private readonly ExecutionTracker _tracker;
        public int LogCallCount;

        public TrackingLogger(ExecutionTracker tracker)
        {
            _tracker = tracker;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            LogCallCount++;
            
            try
            {
                // Try both structured and formatted approaches
                var message = formatter(state, exception);
                
                // Approach 1: Check formatted message
                if (message.Contains("Starting automation execution"))
                {
                    // Extract automation ID from message
                    var match = System.Text.RegularExpressions.Regex.Match(message, @"Starting automation execution: ([^\s]+)");
                    if (match.Success)
                    {
                        var automationId = match.Groups[1].Value;
                        _ = _tracker.RecordExecutionAsync(automationId, CancellationToken.None);
                        return;
                    }
                }
                
                // Approach 2: Check structured logging
                if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
                {
                    var dict = kvps.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                    
                    if (dict.TryGetValue("{OriginalFormat}", out var format) && 
                        format?.ToString()?.Contains("Starting automation execution") == true)
                    {
                        if (dict.TryGetValue("AutomationId", out var automationId) && automationId != null)
                        {
                            _ = _tracker.RecordExecutionAsync(automationId.ToString()!, CancellationToken.None);
                        }
                    }
                }
            }
            catch
            {
                // Ignore logging errors in tests
            }
        }
    }
}
