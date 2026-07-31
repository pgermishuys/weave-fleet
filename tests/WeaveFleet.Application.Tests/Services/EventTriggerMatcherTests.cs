using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class EventTriggerMatcherTests
{
    private readonly FakeAutomationRepository _repository;
    private readonly EventTriggerMatcher _sut;

    public EventTriggerMatcherTests()
    {
        _repository = new FakeAutomationRepository();
        _sut = new EventTriggerMatcher(_repository, NullLogger<EventTriggerMatcher>.Instance);
    }

    [Fact]
    public async Task FindMatchingAutomationsAsync_WhenEventTypeMatches_ReturnsAutomation()
    {
        // Arrange
        _repository.Seed(new Automation
        {
            Id = "auto-1",
            Name = "Session Started Handler",
            TriggerType = "event",
            TriggerConfig = """{"eventType":"session.started"}""",
            IsEnabled = true,
            UserId = "user-1",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        // Act
        var result = await _sut.FindMatchingAutomationsAsync("session.started");

        // Assert
        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe("auto-1");
    }

    [Fact]
    public async Task FindMatchingAutomationsAsync_WhenEventTypeDoesNotMatch_ReturnsEmpty()
    {
        // Arrange
        _repository.Seed(new Automation
        {
            Id = "auto-1",
            Name = "Session Started Handler",
            TriggerType = "event",
            TriggerConfig = """{"eventType":"session.started"}""",
            IsEnabled = true,
            UserId = "user-1",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        // Act
        var result = await _sut.FindMatchingAutomationsAsync("message.created");

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task FindMatchingAutomationsAsync_WhenMultipleAutomationsMatch_ReturnsAll()
    {
        // Arrange
        _repository.Seed(
            new Automation
            {
                Id = "auto-1",
                Name = "Handler 1",
                TriggerType = "event",
                TriggerConfig = """{"eventType":"session.started"}""",
                IsEnabled = true,
                UserId = "user-1",
                CreatedAt = DateTime.UtcNow.ToString("O")
            },
            new Automation
            {
                Id = "auto-2",
                Name = "Handler 2",
                TriggerType = "event",
                TriggerConfig = """{"eventType":"session.started"}""",
                IsEnabled = true,
                UserId = "user-1",
                CreatedAt = DateTime.UtcNow.ToString("O")
            }
        );

        // Act
        var result = await _sut.FindMatchingAutomationsAsync("session.started");

        // Assert
        result.Count.ShouldBe(2);
        result.Select(a => a.Id).ShouldBe(["auto-1", "auto-2"]);
    }

    [Fact]
    public async Task FindMatchingAutomationsAsync_WhenMalformedJson_SkipsGracefully()
    {
        // Arrange
        _repository.Seed(
            new Automation
            {
                Id = "auto-malformed",
                Name = "Malformed Config",
                TriggerType = "event",
                TriggerConfig = """{"eventType":""", // Invalid JSON
                IsEnabled = true,
                UserId = "user-1",
                CreatedAt = DateTime.UtcNow.ToString("O")
            },
            new Automation
            {
                Id = "auto-valid",
                Name = "Valid Config",
                TriggerType = "event",
                TriggerConfig = """{"eventType":"session.started"}""",
                IsEnabled = true,
                UserId = "user-1",
                CreatedAt = DateTime.UtcNow.ToString("O")
            }
        );

        // Act
        var result = await _sut.FindMatchingAutomationsAsync("session.started");

        // Assert
        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe("auto-valid");
    }

    [Fact]
    public async Task FindMatchingAutomationsAsync_WhenEventTypeMissingInConfig_DoesNotMatch()
    {
        // Arrange
        _repository.Seed(new Automation
        {
            Id = "auto-1",
            Name = "No Event Type",
            TriggerType = "event",
            TriggerConfig = """{"otherField":"value"}""",
            IsEnabled = true,
            UserId = "user-1",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        // Act
        var result = await _sut.FindMatchingAutomationsAsync("session.started");

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task FindMatchingAutomationsAsync_IsCaseInsensitive()
    {
        // Arrange
        _repository.Seed(new Automation
        {
            Id = "auto-1",
            Name = "Case Test",
            TriggerType = "event",
            TriggerConfig = """{"eventType":"SESSION.STARTED"}""",
            IsEnabled = true,
            UserId = "user-1",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        // Act
        var result = await _sut.FindMatchingAutomationsAsync("session.started");

        // Assert
        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe("auto-1");
    }

    [Fact]
    public async Task FindMatchingAutomationsAsync_WhenNoEnabledAutomations_ReturnsEmpty()
    {
        // Arrange - repository returns empty list

        // Act
        var result = await _sut.FindMatchingAutomationsAsync("session.started");

        // Assert
        result.ShouldBeEmpty();
    }
}

/// <summary>
/// Fake in-memory automation repository for testing.
/// </summary>
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
