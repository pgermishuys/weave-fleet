using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class DelegationServiceTests
{
    private readonly InMemoryDelegationRepository _delegationRepository = new();
    private readonly FakeEventBroadcaster _eventBroadcaster = new();
    private readonly IUserContext _userContext = new TestUserContext("user-1");
    private readonly DelegationService _sut;

    public DelegationServiceTests()
    {
        _sut = new DelegationService(_delegationRepository, _eventBroadcaster, _userContext);
    }

    [Fact]
    public async Task HandleDelegationDetectedAsync_WhenMissing_CreatesPendingDelegationAndBroadcasts()
    {
        var result = await _sut.HandleDelegationDetectedAsync("parent-1", "tool-1", "Code Review");

        result.ParentToolCallId.ShouldBe("tool-1");
        result.Title.ShouldBe("Code Review");
        result.Status.ShouldBe("pending");

        var inserted = _delegationRepository.All.Single();
        inserted.ParentSessionId.ShouldBe("parent-1");
        inserted.ParentToolCallId.ShouldBe("tool-1");
        inserted.Title.ShouldBe("Code Review");
        inserted.Status.ShouldBe("pending");
        inserted.ChildSessionId.ShouldBeNull();
        inserted.CompletedAt.ShouldBeNull();

        _eventBroadcaster.Broadcasts.Count(b =>
            b.Topic == "session:parent-1" &&
            b.Type == "delegation.created" &&
            b.UserId == "user-1").ShouldBe(1);
    }

    [Fact]
    public async Task HandleDelegationDetectedAsync_WhenExisting_ReturnsExistingWithoutBroadcast()
    {
        var existing = new Delegation
        {
            Id = "del-1",
            ParentSessionId = "parent-1",
            ParentToolCallId = "tool-1",
            Title = "Code Review",
            Status = "pending",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        };
        _delegationRepository.Seed(existing);

        var result = await _sut.HandleDelegationDetectedAsync("parent-1", "tool-1", "Ignored");

        result.DelegationId.ShouldBe("del-1");
        result.Title.ShouldBe("Code Review");

        // No new insert (only the seeded one)
        _delegationRepository.All.Count.ShouldBe(1);
        _eventBroadcaster.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleChildLinkedAsync_FromPending_UpdatesChildAndBroadcastsRunning()
    {
        var delegation = new Delegation
        {
            Id = "del-1",
            ParentSessionId = "parent-1",
            ParentToolCallId = "tool-1",
            Title = "Code Review",
            Status = "pending",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        };
        _delegationRepository.Seed(delegation);

        var result = await _sut.HandleChildLinkedAsync("parent-1", "tool-1", "child-1");

        result.ShouldNotBeNull();
        result!.ChildSessionId.ShouldBe("child-1");
        result.Status.ShouldBe("running");

        var stored = _delegationRepository.All.Single();
        stored.ChildSessionId.ShouldBe("child-1");
        stored.Status.ShouldBe("running");

        _eventBroadcaster.Broadcasts.Count(b =>
            b.Topic == "session:parent-1" &&
            b.Type == "delegation.updated" &&
            b.UserId == "user-1").ShouldBe(1);
    }

    [Fact]
    public async Task HandleDelegationFinishedAsync_FromRunning_TransitionsToTerminalAndBroadcasts()
    {
        var delegation = new Delegation
        {
            Id = "del-1",
            ParentSessionId = "parent-1",
            ParentToolCallId = "tool-1",
            ChildSessionId = "child-1",
            Title = "Code Review",
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        };
        _delegationRepository.Seed(delegation);

        var result = await _sut.HandleDelegationFinishedAsync("del-1", "completed");

        result.ShouldNotBeNull();
        result!.Status.ShouldBe("completed");

        var stored = _delegationRepository.All.Single();
        stored.Status.ShouldBe("completed");

        _eventBroadcaster.Broadcasts.Count(b =>
            b.Topic == "session:parent-1" &&
            b.Type == "delegation.updated" &&
            b.UserId == "user-1").ShouldBe(1);
    }

    [Fact]
    public async Task HandleDelegationFinishedAsync_WhenAlreadyTerminalWithDifferentStatus_Throws()
    {
        _delegationRepository.Seed(new Delegation
        {
            Id = "del-1",
            ParentSessionId = "parent-1",
            Title = "Code Review",
            Status = "completed",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        });

        var act = () => _sut.HandleDelegationFinishedAsync("del-1", "error");

        await Should.ThrowAsync<InvalidOperationException>(act);
    }
}
