using NSubstitute;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class DelegationServiceTests
{
    private readonly IDelegationRepository _delegationRepository = Substitute.For<IDelegationRepository>();
    private readonly IEventBroadcaster _eventBroadcaster = Substitute.For<IEventBroadcaster>();
    private readonly IUserContext _userContext = new TestUserContext("user-1");
    private readonly DelegationService _sut;

    public DelegationServiceTests()
    {
        _sut = new DelegationService(_delegationRepository, _eventBroadcaster, _userContext);
    }

    [Fact]
    public async Task HandleDelegationDetectedAsync_WhenMissing_CreatesPendingDelegationAndBroadcasts()
    {
        _delegationRepository.GetByParentToolCallIdAsync("parent-1", "tool-1")
            .Returns((Delegation?)null);

        var result = await _sut.HandleDelegationDetectedAsync("parent-1", "tool-1", "Code Review");

        result.ParentToolCallId.ShouldBe("tool-1");
        result.Title.ShouldBe("Code Review");
        result.Status.ShouldBe("pending");

        await _delegationRepository.Received(1).InsertAsync(Arg.Is<Delegation>(d =>
            d.ParentSessionId == "parent-1" &&
            d.ParentToolCallId == "tool-1" &&
            d.Title == "Code Review" &&
            d.Status == "pending" &&
            d.ChildSessionId == null &&
            d.CompletedAt == null));

        await _eventBroadcaster.Received(1).BroadcastAsync(
            "session:parent-1",
            "delegation.created",
            Arg.Is<DelegationEventDto>(e =>
                e.ParentSessionId == "parent-1" &&
                e.ParentToolCallId == "tool-1" &&
                e.Title == "Code Review" &&
                e.Status == "pending"),
            "user-1",
            Arg.Any<CancellationToken>());
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
        _delegationRepository.GetByParentToolCallIdAsync("parent-1", "tool-1")
            .Returns(existing);

        var result = await _sut.HandleDelegationDetectedAsync("parent-1", "tool-1", "Ignored");

        result.DelegationId.ShouldBe("del-1");
        result.Title.ShouldBe("Code Review");

        await _delegationRepository.DidNotReceive().InsertAsync(Arg.Any<Delegation>());
        await _eventBroadcaster.DidNotReceive().BroadcastAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<object>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
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
        _delegationRepository.GetByParentToolCallIdAsync("parent-1", "tool-1")
            .Returns(delegation);

        var result = await _sut.HandleChildLinkedAsync("parent-1", "tool-1", "child-1");

        result.ShouldNotBeNull();
        result!.ChildSessionId.ShouldBe("child-1");
        result.Status.ShouldBe("running");

        await _delegationRepository.Received(1).UpdateChildSessionIdAsync(
            "del-1",
            "child-1",
            Arg.Any<string>());
        await _delegationRepository.Received(1).UpdateStatusAsync(
            Arg.Is("del-1"),
            Arg.Is("running"),
            Arg.Any<string>(),
            Arg.Is<string?>(s => s == null));
        await _eventBroadcaster.Received(1).BroadcastAsync(
            "session:parent-1",
            "delegation.updated",
            Arg.Is<DelegationEventDto>(e =>
                e.DelegationId == "del-1" &&
                e.ChildSessionId == "child-1" &&
                e.Status == "running"),
            "user-1",
            Arg.Any<CancellationToken>());
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
        _delegationRepository.GetByIdAsync("del-1").Returns(delegation);

        var result = await _sut.HandleDelegationFinishedAsync("del-1", "completed");

        result.ShouldNotBeNull();
        result!.Status.ShouldBe("completed");

        await _delegationRepository.Received(1).UpdateStatusAsync(
            Arg.Is("del-1"),
            Arg.Is("completed"),
            Arg.Any<string>(),
            Arg.Any<string>());
        await _eventBroadcaster.Received(1).BroadcastAsync(
            "session:parent-1",
            "delegation.updated",
            Arg.Is<DelegationEventDto>(e =>
                e.DelegationId == "del-1" &&
                e.Status == "completed" &&
                e.ChildSessionId == "child-1"),
            "user-1",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleDelegationFinishedAsync_WhenAlreadyTerminalWithDifferentStatus_Throws()
    {
        _delegationRepository.GetByIdAsync("del-1").Returns(new Delegation
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
