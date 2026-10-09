using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class TurnFailureRecorderTests
{
    private readonly InMemoryMessageRepository _messages = new();
    private readonly TurnFailureRecorder _sut;

    public TurnFailureRecorderTests()
    {
        _sut = new TurnFailureRecorder(
            TestServiceScopeFactory.Create(services =>
            {
                services.AddSingleton<IBackgroundUserScope>(new NoUserScope());
                services.AddSingleton<IMessageRepository>(_messages);
            }),
            TimeProvider.System,
            NullLogger<TurnFailureRecorder>.Instance);
    }

    private static TurnFailed Failed(string? messageId = null) => new()
    {
        Payload = new TurnFailedPayload
        {
            SessionId = "session-1",
            MessageId = messageId,
            Error = new TurnError { Name = "ProviderModelNotFoundError", Message = "Model not found" },
        },
    };

    [Fact]
    public async Task keeps_a_failed_turns_failure_as_an_assistant_message_of_the_session()
    {
        _sut.Observe("session-1", "user-1", Failed());
        await _sut.Pending;

        var kept = MessagePersistenceService.ToHarnessMessage(_messages.All.ShouldHaveSingleItem());
        _messages.All[0].SessionId.ShouldBe("session-1");
        kept.Role.ShouldBe("assistant");
        kept.Parts.ShouldBeEmpty();
        kept.Error.ShouldNotBeNull().Message.ShouldBe("Model not found");
        // The conversation orders messages by id, and OpenCode's ids ascend with time: a kept failure's id must too.
        kept.Id.ShouldStartWith("msg_");
    }

    [Fact]
    public async Task keeps_nothing_for_other_events_or_without_an_owner()
    {
        _sut.Observe("session-1", "user-1", new SessionIdled { Payload = new SessionIdledPayload { SessionId = "session-1" } });
        _sut.Observe("session-1", null, Failed());
        await _sut.Pending;

        _messages.All.ShouldBeEmpty();
    }

    private sealed class NoUserScope : IBackgroundUserScope
    {
        public IDisposable Begin(string userId) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
