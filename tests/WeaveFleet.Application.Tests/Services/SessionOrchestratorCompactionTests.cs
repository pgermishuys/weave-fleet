using Shouldly;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// Compact now goes to the session's harness only between turns, when a prompt could go: the session isn't archived
/// and its harness can compact.
/// </summary>
public sealed class SessionOrchestratorCompactionTests : IAsyncDisposable
{
    private readonly SessionOrchestratorBuilder _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
    private readonly FakeHarnessSession _harnessSession = new("inst-1");

    public ValueTask DisposeAsync() => _harnessSession.DisposeAsync();

    private void Seed(string harnessType = "opencode", string retentionStatus = "active")
    {
        _builder.SessionRepository.Seed(new Session
        {
            Id = "s1",
            InstanceId = "inst-1",
            Title = "T",
            Status = "active",
            Directory = "/tmp",
            CreatedAt = "2026-01-01",
            RetentionStatus = retentionStatus,
            HarnessType = harnessType,
            UserId = "user-1",
            SelectedProviderId = "anthropic",
            SelectedModelId = "claude-opus-5",
        });
        _builder.InstanceTracker.Register("inst-1", _harnessSession);
    }

    private void RegisterHarness(string type, bool supportsCompaction)
        => _builder.RegisterHarness(type, type == "opencode" ? "OpenCode" : "Other", new HarnessCapabilities { SupportsCompaction = supportsCompaction });

    [Fact]
    public async Task asks_the_harness_to_compact_with_the_sessions_model()
    {
        RegisterHarness("opencode", supportsCompaction: true);
        Seed();

        var result = await _builder.Build().CompactAsync("s1");

        result.IsSuccess.ShouldBeTrue();
        var call = _harnessSession.CompactCalls.ShouldHaveSingleItem();
        call.ModelId.ShouldBe("claude-opus-5");
        call.ProviderId.ShouldBe("anthropic");
    }

    [Fact]
    public async Task refuses_an_archived_session()
    {
        RegisterHarness("opencode", supportsCompaction: true);
        Seed(retentionStatus: "archived");

        var result = await _builder.Build().CompactAsync("s1");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.Session.RetentionStatus");
        _harnessSession.CompactCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task refuses_a_harness_that_cant_compact()
    {
        RegisterHarness("other", supportsCompaction: false);
        Seed(harnessType: "other");

        var result = await _builder.Build().CompactAsync("s1");

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldBe("Other can't be asked to compact its context.");
        _harnessSession.CompactCalls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("retry")]
    public async Task waits_for_the_turn_to_end(string activity)
    {
        RegisterHarness("opencode", supportsCompaction: true);
        Seed();
        _builder.ActivityTracker.Update("s1", activity, "user-1");

        var result = await _builder.Build().CompactAsync("s1");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("General.Conflict");
        _harnessSession.CompactCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task says_why_when_the_harness_cant_compact_this_session()
    {
        RegisterHarness("opencode", supportsCompaction: true);
        Seed();
        _harnessSession.CompactBehavior = (_, _) => throw new NotSupportedException("OpenCode compacts with a model, and this session has none yet.");

        var result = await _builder.Build().CompactAsync("s1");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.Session.Compact");
        result.Error.Description.ShouldBe("OpenCode compacts with a model, and this session has none yet.");
    }
}
