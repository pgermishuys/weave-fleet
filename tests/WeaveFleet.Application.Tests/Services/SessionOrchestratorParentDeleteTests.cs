using Shouldly;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// A delegated child is a hidden Fleet session that nothing lists once its parent is gone, so deleting the parent
/// has to take it along: its Fleet row, and the harness session it runs.
/// </summary>
public sealed class SessionOrchestratorParentDeleteTests : IAsyncDisposable
{
    private readonly SessionOrchestratorBuilder _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
    private readonly FakeHarnessSession _parent = new("inst-parent");
    private readonly FakeHarnessSession _child = new("inst-child");

    public async ValueTask DisposeAsync()
    {
        await _parent.DisposeAsync();
        await _child.DisposeAsync();
    }

    private void Seed()
    {
        _builder.InstanceRepository.GetByIdBehavior = id => Task.FromResult<Instance?>(new Instance
        {
            Id = id,
            Port = 0,
            Directory = "/tmp",
            Url = string.Empty,
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });
        _builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities());
        _builder.WorkspaceRepository.Seed(new Workspace { Id = "ws-1", Directory = "/tmp/repo", IsolationStrategy = "worktree", SourceDirectory = "/tmp/src" });
        _builder.SessionRepository.Seed(Session("p1", "inst-parent", parentId: null));
        _builder.SessionRepository.Seed(Session("c1", "inst-child", parentId: "p1"));
        _builder.InstanceTracker.Register("inst-parent", _parent);
        _builder.InstanceTracker.Register("inst-child", _child);
    }

    private static Session Session(string id, string instanceId, string? parentId) => new()
    {
        Id = id,
        WorkspaceId = "ws-1",
        InstanceId = instanceId,
        Title = id,
        Status = "active",
        Directory = "/tmp/repo",
        CreatedAt = "2026-01-01",
        HarnessType = "opencode",
        UserId = "user-1",
        ParentSessionId = parentId,
        IsHidden = parentId is not null,
    };

    [Fact]
    public async Task deleting_the_parent_deletes_its_delegated_children_too()
    {
        Seed();
        var orchestrator = _builder.Build();

        (await orchestrator.DeleteSessionAsync("p1")).IsSuccess.ShouldBeTrue();

        _parent.DeleteCalled.ShouldBeTrue();
        (await _builder.SessionRepository.GetByIdAsync("c1")).ShouldBeNull();
        _child.DeleteCalled.ShouldBeTrue();
    }
}
