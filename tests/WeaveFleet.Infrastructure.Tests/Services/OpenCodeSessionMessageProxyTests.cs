using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Services;

public sealed class OpenCodeSessionMessageProxyTests
{
    private static ServiceProvider CreateServiceProvider(ISessionActivator sessionActivator)
    {
        var services = new ServiceCollection();
        services.AddScoped<ISessionActivator>(_ => sessionActivator);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task GetSnapshotAsync_returns_messages_from_live_harness_when_available()
    {
        // Arrange
        var sessionId = "test-session-1";
        var instanceId = "instance-1";
        var session = new Session
        {
            Id = sessionId,
            InstanceId = instanceId,
            HarnessType = "opencode",
            Title = "Test Session",
            Status = "active",
            UserId = "user-1",
        };

        var sessionRepository = new InMemorySessionRepository();
        sessionRepository.Seed(session);

        var harnessSession = new FakeHarnessSession(instanceId)
        {
            GetMessagesBehavior = (query, ct) => Task.FromResult(new MessagePage(
                [
                    new HarnessMessage
                    {
                        Id = "msg-1",
                        Role = "user",
                        Parts = [new TextPart("Hello")],
                        Timestamp = DateTimeOffset.UtcNow,
                    }
                ],
                false))
        };

        var instanceTracker = new InstanceTracker();
        instanceTracker.Register(instanceId, harnessSession);

        var activityTracker = new SessionActivityTracker();
        activityTracker.Update(sessionId, "idle", "user-1");

        var delegationRepository = new InMemoryDelegationRepository();

        var fallbackSnapshotBuilder = new FakeSessionSnapshotBuilder();

        var sessionActivator = new FakeSessionActivator();
        var serviceProvider = CreateServiceProvider(sessionActivator);

        var proxy = new OpenCodeSessionMessageProxy(
            sessionRepository,
            instanceTracker,
            activityTracker,
            delegationRepository,
            fallbackSnapshotBuilder,
            serviceProvider,
            NullLogger<OpenCodeSessionMessageProxy>.Instance);

        // Act
        var snapshot = await proxy.GetSnapshotAsync(sessionId);

        // Assert
        snapshot.ShouldNotBeNull();
        snapshot.Session.Id.ShouldBe(sessionId);
        snapshot.Messages.ShouldNotBeEmpty();
        snapshot.Messages.Count.ShouldBe(1);
        snapshot.Messages[0].Info.Id.ShouldBe("msg-1");
        snapshot.Messages[0].Info.Role.ShouldBe("user");
        snapshot.Messages[0].Parts.Count.ShouldBe(1);
        snapshot.ActivityStatus.ShouldBe("idle");
        snapshot.HasMore.ShouldBeFalse();
        snapshot.IsPartial.ShouldBeFalse(); // Live harness data should not be partial

        // Verify fallback was NOT called
        fallbackSnapshotBuilder.BuildAsyncCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetSnapshotAsync_falls_back_to_persisted_when_harness_unavailable()
    {
        // Arrange
        var sessionId = "test-session-2";
        var instanceId = "instance-2";
        var session = new Session
        {
            Id = sessionId,
            InstanceId = instanceId,
            HarnessType = "opencode",
            Title = "Test Session",
            Status = "active",
            UserId = "user-1",
        };

        var sessionRepository = new InMemorySessionRepository();
        sessionRepository.Seed(session);

        var harnessSession = new FakeHarnessSession(instanceId)
        {
            GetMessagesBehavior = (query, ct) => throw new HttpRequestException("Connection refused")
        };

        var instanceTracker = new InstanceTracker();
        instanceTracker.Register(instanceId, harnessSession);

        var activityTracker = new SessionActivityTracker();

        var delegationRepository = new InMemoryDelegationRepository();

        var fallbackSnapshot = new SessionSnapshot
        {
            Session = new SessionSnapshotSession
            {
                Id = sessionId,
                Title = "Test Session",
                Status = "active",
            },
            Messages =
            [
                new MessageLifecyclePayload
                {
                    Info = new MessageEventInfo
                    {
                        Id = "msg-persisted",
                        Role = "user",
                        SessionId = sessionId,
                        Time = new MessageEventTime { Created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                    },
                    Parts = [new TextMessageEventPart
                    {
                        Id = "part-1",
                        SessionId = sessionId,
                        MessageId = "msg-persisted",
                        Text = "Persisted message",
                    }],
                }
            ],
            ActivityStatus = "idle",
            HasMore = false,
            IsPartial = false,
        };

        var fallbackSnapshotBuilder = new FakeSessionSnapshotBuilder
        {
            BuildBehavior = (sid, pageSize, cursor) => Task.FromResult(fallbackSnapshot)
        };

        var sessionActivator = new FakeSessionActivator();
        var serviceProvider = CreateServiceProvider(sessionActivator);

        var proxy = new OpenCodeSessionMessageProxy(
            sessionRepository,
            instanceTracker,
            activityTracker,
            delegationRepository,
            fallbackSnapshotBuilder,
            serviceProvider,
            NullLogger<OpenCodeSessionMessageProxy>.Instance);

        // Act
        var snapshot = await proxy.GetSnapshotAsync(sessionId);

        // Assert
        snapshot.ShouldNotBeNull();
        snapshot.Session.Id.ShouldBe(sessionId);
        snapshot.Messages.ShouldNotBeEmpty();
        snapshot.Messages.Count.ShouldBe(1);
        snapshot.Messages[0].Info.Id.ShouldBe("msg-persisted");
        snapshot.IsPartial.ShouldBeTrue(); // Snapshot should be marked as partial when harness is unavailable

        // Verify fallback WAS called
        fallbackSnapshotBuilder.BuildAsyncCalls.ShouldNotBeEmpty();
        fallbackSnapshotBuilder.BuildAsyncCalls[0].SessionId.ShouldBe(sessionId);
    }

    [Fact]
    public async Task GetSnapshotAsync_uses_persisted_for_non_opencode_sessions()
    {
        // Arrange
        var sessionId = "test-session-3";
        var session = new Session
        {
            Id = sessionId,
            InstanceId = "instance-3",
            HarnessType = "nucode",
            Title = "NuCode Session",
            Status = "active",
            UserId = "user-1",
        };

        var sessionRepository = new InMemorySessionRepository();
        sessionRepository.Seed(session);

        var instanceTracker = new InstanceTracker();
        var activityTracker = new SessionActivityTracker();
        var delegationRepository = new InMemoryDelegationRepository();

        var fallbackSnapshot = new SessionSnapshot
        {
            Session = new SessionSnapshotSession
            {
                Id = sessionId,
                Title = "NuCode Session",
                Status = "active",
            },
            Messages = [],
            ActivityStatus = "idle",
            HasMore = false,
            IsPartial = false,
        };

        var fallbackSnapshotBuilder = new FakeSessionSnapshotBuilder
        {
            BuildBehavior = (sid, pageSize, cursor) => Task.FromResult(fallbackSnapshot)
        };

        var sessionActivator = new FakeSessionActivator();
        var serviceProvider = CreateServiceProvider(sessionActivator);

        var proxy = new OpenCodeSessionMessageProxy(
            sessionRepository,
            instanceTracker,
            activityTracker,
            delegationRepository,
            fallbackSnapshotBuilder,
            serviceProvider,
            NullLogger<OpenCodeSessionMessageProxy>.Instance);

        // Act
        var snapshot = await proxy.GetSnapshotAsync(sessionId);

        // Assert
        snapshot.ShouldNotBeNull();
        snapshot.Session.Id.ShouldBe(sessionId);

        // Verify fallback WAS called for non-opencode session
        fallbackSnapshotBuilder.BuildAsyncCalls.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task GetMessagesAsync_returns_messages_from_live_harness_when_available()
    {
        // Arrange
        var sessionId = "test-session-4";
        var instanceId = "instance-4";
        var session = new Session
        {
            Id = sessionId,
            InstanceId = instanceId,
            HarnessType = "opencode",
            Title = "Test Session",
            Status = "active",
            UserId = "user-1",
        };

        var sessionRepository = new InMemorySessionRepository();
        sessionRepository.Seed(session);

        MessageQuery? capturedQuery = null;
        var harnessSession = new FakeHarnessSession(instanceId)
        {
            GetMessagesBehavior = (query, ct) =>
            {
                capturedQuery = query;
                return Task.FromResult(new MessagePage(
                    [
                        new HarnessMessage
                        {
                            Id = "msg-1",
                            Role = "assistant",
                            Parts = [new TextPart("Response")],
                            Timestamp = DateTimeOffset.UtcNow,
                            Agent = "loom",
                            ModelId = "claude-sonnet-4",
                        }
                    ],
                    true));
            }
        };

        var instanceTracker = new InstanceTracker();
        instanceTracker.Register(instanceId, harnessSession);

        var activityTracker = new SessionActivityTracker();
        var delegationRepository = new InMemoryDelegationRepository();
        var fallbackSnapshotBuilder = new FakeSessionSnapshotBuilder();

        var sessionActivator = new FakeSessionActivator();
        var serviceProvider = CreateServiceProvider(sessionActivator);

        var proxy = new OpenCodeSessionMessageProxy(
            sessionRepository,
            instanceTracker,
            activityTracker,
            delegationRepository,
            fallbackSnapshotBuilder,
            serviceProvider,
            NullLogger<OpenCodeSessionMessageProxy>.Instance);

        // Act
        var result = await proxy.GetMessagesAsync(sessionId, limit: 50);

        // Assert
        result.ShouldNotBeNull();
        result.Messages.ShouldNotBeEmpty();
        result.Messages.Count.ShouldBe(1);
        result.Messages[0].Id.ShouldBe("msg-1");
        result.Messages[0].Role.ShouldBe("assistant");
        result.Messages[0].Agent.ShouldBe("loom");
        result.Messages[0].ModelId.ShouldBe("claude-sonnet-4");
        result.HasMore.ShouldBeTrue();

        // Verify harness was called with correct query
        capturedQuery.ShouldNotBeNull();
        capturedQuery.Limit.ShouldBe(50);
    }

    [Fact]
    public async Task GetMessagesAsync_falls_back_to_persisted_when_harness_unavailable()
    {
        // Arrange
        var sessionId = "test-session-5";
        var instanceId = "instance-5";
        var session = new Session
        {
            Id = sessionId,
            InstanceId = instanceId,
            HarnessType = "opencode",
            Title = "Test Session",
            Status = "active",
            UserId = "user-1",
        };

        var sessionRepository = new InMemorySessionRepository();
        sessionRepository.Seed(session);

        var harnessSession = new FakeHarnessSession(instanceId)
        {
            GetMessagesBehavior = (query, ct) => throw new TaskCanceledException("Timeout")
        };

        var instanceTracker = new InstanceTracker();
        instanceTracker.Register(instanceId, harnessSession);

        var activityTracker = new SessionActivityTracker();
        var delegationRepository = new InMemoryDelegationRepository();

        var fallbackSnapshot = new SessionSnapshot
        {
            Session = new SessionSnapshotSession
            {
                Id = sessionId,
                Title = "Test Session",
                Status = "active",
            },
            Messages =
            [
                new MessageLifecyclePayload
                {
                    Info = new MessageEventInfo
                    {
                        Id = "msg-fallback",
                        Role = "user",
                        SessionId = sessionId,
                        Time = new MessageEventTime { Created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                    },
                    Parts = [],
                }
            ],
            ActivityStatus = "idle",
            HasMore = false,
            IsPartial = false,
        };

        var fallbackSnapshotBuilder = new FakeSessionSnapshotBuilder
        {
            BuildBehavior = (sid, pageSize, cursor) => Task.FromResult(fallbackSnapshot)
        };

        var sessionActivator = new FakeSessionActivator();
        var serviceProvider = CreateServiceProvider(sessionActivator);

        var proxy = new OpenCodeSessionMessageProxy(
            sessionRepository,
            instanceTracker,
            activityTracker,
            delegationRepository,
            fallbackSnapshotBuilder,
            serviceProvider,
            NullLogger<OpenCodeSessionMessageProxy>.Instance);

        // Act
        var result = await proxy.GetMessagesAsync(sessionId);

        // Assert
        result.ShouldNotBeNull();
        result.Messages.ShouldNotBeEmpty();
        result.Messages[0].Id.ShouldBe("msg-fallback");
        result.HasMore.ShouldBeFalse();

        // Verify fallback WAS called
        fallbackSnapshotBuilder.BuildAsyncCalls.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task GetSnapshotAsync_maps_all_part_types_correctly()
    {
        // Arrange
        var sessionId = "test-session-6";
        var instanceId = "instance-6";
        var session = new Session
        {
            Id = sessionId,
            InstanceId = instanceId,
            HarnessType = "opencode",
            Title = "Test Session",
            Status = "active",
            UserId = "user-1",
        };

        var sessionRepository = new InMemorySessionRepository();
        sessionRepository.Seed(session);

        var timestamp = DateTimeOffset.UtcNow;
        var toolArgs = JsonDocument.Parse("""{"path": "/test/file.txt"}""").RootElement;

        var harnessSession = new FakeHarnessSession(instanceId)
        {
            GetMessagesBehavior = (query, ct) => Task.FromResult(new MessagePage(
                [
                    new HarnessMessage
                    {
                        Id = "msg-mixed",
                        Role = "assistant",
                        Parts =
                        [
                            new TextPart("Starting work"),
                            new ReasoningPart("Internal reasoning text", "Summary of reasoning"),
                            new ToolUsePart("tool-call-1", "read", toolArgs, ToolUseState.Completed),
                            new FilePart("file-1", "image/png", "https://example.com/image.png", "screenshot.png"),
                            new StepFinishPart(0, "end_turn", 0.05, 100, 50, 0, timestamp.ToUnixTimeMilliseconds()),
                            new TextPart("Work complete"),
                        ],
                        Timestamp = timestamp,
                        Agent = "shuttle",
                        ModelId = "claude-sonnet-4",
                    }
                ],
                false))
        };

        var instanceTracker = new InstanceTracker();
        instanceTracker.Register(instanceId, harnessSession);

        var activityTracker = new SessionActivityTracker();
        var delegationRepository = new InMemoryDelegationRepository();
        var fallbackSnapshotBuilder = new FakeSessionSnapshotBuilder();

        var sessionActivator = new FakeSessionActivator();
        var serviceProvider = CreateServiceProvider(sessionActivator);

        var proxy = new OpenCodeSessionMessageProxy(
            sessionRepository,
            instanceTracker,
            activityTracker,
            delegationRepository,
            fallbackSnapshotBuilder,
            serviceProvider,
            NullLogger<OpenCodeSessionMessageProxy>.Instance);

        // Act
        var snapshot = await proxy.GetSnapshotAsync(sessionId);

        // Assert
        snapshot.ShouldNotBeNull();
        snapshot.Messages.Count.ShouldBe(1);

        var message = snapshot.Messages[0];
        message.Info.Id.ShouldBe("msg-mixed");
        message.Info.Role.ShouldBe("assistant");
        message.Info.Agent.ShouldBe("shuttle");
        message.Info.ModelId.ShouldBe("claude-sonnet-4");

        // Verify reasoning parts are filtered out
        message.Parts.ShouldNotContain(p => p is ReasoningMessageEventPart);

        // Verify all other part types are present
        message.Parts.Count.ShouldBe(5); // 2 text + 1 tool + 1 file + 1 step-finish

        var textParts = message.Parts.OfType<TextMessageEventPart>().ToList();
        textParts.Count.ShouldBe(2);
        textParts[0].Text.ShouldBe("Starting work");
        textParts[0].Id.ShouldBe("msg-mixed-text-0");
        textParts[1].Text.ShouldBe("Work complete");
        textParts[1].Id.ShouldBe("msg-mixed-text-1");

        var toolPart = message.Parts.OfType<ToolMessageEventPart>().Single();
        toolPart.ToolName.ShouldBe("read");
        toolPart.CallId.ShouldBe("tool-call-1");
        toolPart.Id.ShouldBe("msg-mixed-tool-0");
        toolPart.State.ShouldBeOfType<ToolCompletedState>();

        var filePart = message.Parts.OfType<FileMessageEventPart>().Single();
        filePart.Mime.ShouldBe("image/png");
        filePart.Url.ShouldBe("https://example.com/image.png");
        filePart.Filename.ShouldBe("screenshot.png");
        filePart.Id.ShouldBe("file-1");

        var stepPart = message.Parts.OfType<StepFinishedMessageEventPart>().Single();
        stepPart.Index.ShouldBe(0);
        stepPart.Reason.ShouldBe("end_turn");
        stepPart.Cost.ShouldBe(0.05);
        stepPart.Tokens.ShouldNotBeNull();
        stepPart.Tokens!.Input.ShouldBe(100);
        stepPart.Tokens.Output.ShouldBe(50);
        stepPart.Tokens.Reasoning.ShouldBe(0);
        stepPart.CompletedAt.ShouldBe(timestamp.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task GetSnapshotAsync_filters_reasoning_parts_only()
    {
        // Arrange
        var sessionId = "test-session-7";
        var instanceId = "instance-7";
        var session = new Session
        {
            Id = sessionId,
            InstanceId = instanceId,
            HarnessType = "opencode",
            Title = "Test Session",
            Status = "active",
            UserId = "user-1",
        };

        var sessionRepository = new InMemorySessionRepository();
        sessionRepository.Seed(session);

        var harnessSession = new FakeHarnessSession(instanceId)
        {
            GetMessagesBehavior = (query, ct) => Task.FromResult(new MessagePage(
                [
                    new HarnessMessage
                    {
                        Id = "msg-reasoning-only",
                        Role = "assistant",
                        Parts =
                        [
                            new ReasoningPart("First reasoning block", null),
                            new ReasoningPart("Second reasoning block", "Summary"),
                        ],
                        Timestamp = DateTimeOffset.UtcNow,
                    }
                ],
                false))
        };

        var instanceTracker = new InstanceTracker();
        instanceTracker.Register(instanceId, harnessSession);

        var activityTracker = new SessionActivityTracker();
        var delegationRepository = new InMemoryDelegationRepository();
        var fallbackSnapshotBuilder = new FakeSessionSnapshotBuilder();

        var sessionActivator = new FakeSessionActivator();
        var serviceProvider = CreateServiceProvider(sessionActivator);

        var proxy = new OpenCodeSessionMessageProxy(
            sessionRepository,
            instanceTracker,
            activityTracker,
            delegationRepository,
            fallbackSnapshotBuilder,
            serviceProvider,
            NullLogger<OpenCodeSessionMessageProxy>.Instance);

        // Act
        var snapshot = await proxy.GetSnapshotAsync(sessionId);

        // Assert
        snapshot.ShouldNotBeNull();
        snapshot.Messages.Count.ShouldBe(1);

        var message = snapshot.Messages[0];
        message.Info.Id.ShouldBe("msg-reasoning-only");

        // All reasoning parts should be filtered out
        message.Parts.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetSnapshotAsync_preserves_tool_state_mapping()
    {
        // Arrange
        var sessionId = "test-session-8";
        var instanceId = "instance-8";
        var session = new Session
        {
            Id = sessionId,
            InstanceId = instanceId,
            HarnessType = "opencode",
            Title = "Test Session",
            Status = "active",
            UserId = "user-1",
        };

        var sessionRepository = new InMemorySessionRepository();
        sessionRepository.Seed(session);

        var toolArgs = JsonDocument.Parse("""{"command": "ls"}""").RootElement;

        var harnessSession = new FakeHarnessSession(instanceId)
        {
            GetMessagesBehavior = (query, ct) => Task.FromResult(new MessagePage(
                [
                    new HarnessMessage
                    {
                        Id = "msg-tools",
                        Role = "assistant",
                        Parts =
                        [
                            new ToolUsePart("call-1", "bash", toolArgs.Clone(), ToolUseState.Pending),
                            new ToolUsePart("call-2", "bash", toolArgs.Clone(), ToolUseState.Running),
                            new ToolUsePart("call-3", "bash", toolArgs.Clone(), ToolUseState.Completed),
                            new ToolUsePart("call-4", "bash", toolArgs.Clone(), ToolUseState.Error),
                        ],
                        Timestamp = DateTimeOffset.UtcNow,
                    }
                ],
                false))
        };

        var instanceTracker = new InstanceTracker();
        instanceTracker.Register(instanceId, harnessSession);

        var activityTracker = new SessionActivityTracker();
        var delegationRepository = new InMemoryDelegationRepository();
        var fallbackSnapshotBuilder = new FakeSessionSnapshotBuilder();

        var sessionActivator = new FakeSessionActivator();
        var serviceProvider = CreateServiceProvider(sessionActivator);

        var proxy = new OpenCodeSessionMessageProxy(
            sessionRepository,
            instanceTracker,
            activityTracker,
            delegationRepository,
            fallbackSnapshotBuilder,
            serviceProvider,
            NullLogger<OpenCodeSessionMessageProxy>.Instance);

        // Act
        var snapshot = await proxy.GetSnapshotAsync(sessionId);

        // Assert
        snapshot.ShouldNotBeNull();
        snapshot.Messages.Count.ShouldBe(1);

        var message = snapshot.Messages[0];
        var toolParts = message.Parts.OfType<ToolMessageEventPart>().ToList();
        toolParts.Count.ShouldBe(4);

        toolParts[0].CallId.ShouldBe("call-1");
        toolParts[0].State.ShouldBeOfType<ToolPendingState>();
        ((ToolPendingState)toolParts[0].State).Input.ShouldNotBeNull();

        toolParts[1].CallId.ShouldBe("call-2");
        toolParts[1].State.ShouldBeOfType<ToolRunningState>();
        ((ToolRunningState)toolParts[1].State).Input.ShouldNotBeNull();

        toolParts[2].CallId.ShouldBe("call-3");
        toolParts[2].State.ShouldBeOfType<ToolCompletedState>();
        ((ToolCompletedState)toolParts[2].State).Input.ShouldNotBeNull();

        toolParts[3].CallId.ShouldBe("call-4");
        toolParts[3].State.ShouldBeOfType<ToolErrorState>();
        ((ToolErrorState)toolParts[3].State).Input.ShouldNotBeNull();
    }

    [Fact]
    public async Task GetSnapshotAsync_attempts_resume_when_harness_missing_but_resume_token_exists()
    {
        // Arrange
        var sessionId = "test-session-resume-1";
        var instanceId = "instance-resume-1";
        var session = new Session
        {
            Id = sessionId,
            InstanceId = instanceId,
            HarnessType = "opencode",
            HarnessResumeToken = "resume-token-123",
            Title = "Test Session",
            Status = "active",
            UserId = "user-1",
        };

        var sessionRepository = new InMemorySessionRepository();
        sessionRepository.Seed(session);

        var harnessSession = new FakeHarnessSession(instanceId)
        {
            GetMessagesBehavior = (query, ct) => Task.FromResult(new MessagePage(
                [
                    new HarnessMessage
                    {
                        Id = "msg-resumed",
                        Role = "user",
                        Parts = [new TextPart("Resumed message")],
                        Timestamp = DateTimeOffset.UtcNow,
                    }
                ],
                false))
        };

        var instanceTracker = new InstanceTracker();
        // Initially no harness registered

        var activityTracker = new SessionActivityTracker();
        var delegationRepository = new InMemoryDelegationRepository();
        var fallbackSnapshotBuilder = new FakeSessionSnapshotBuilder();

        var sessionActivator = new FakeSessionActivator
        {
            ActivateBehavior = (sid, ct) =>
            {
                // Simulate successful resume by registering the harness
                instanceTracker.Register(instanceId, harnessSession);
                return Task.FromResult(Result.Success<IHarnessSession>(harnessSession));
            }
        };
        var serviceProvider = CreateServiceProvider(sessionActivator);

        var proxy = new OpenCodeSessionMessageProxy(
            sessionRepository,
            instanceTracker,
            activityTracker,
            delegationRepository,
            fallbackSnapshotBuilder,
            serviceProvider,
            NullLogger<OpenCodeSessionMessageProxy>.Instance);

        // Act
        var snapshot = await proxy.GetSnapshotAsync(sessionId);

        // Assert
        snapshot.ShouldNotBeNull();
        snapshot.Messages.Count.ShouldBe(1);
        snapshot.Messages[0].Info.Id.ShouldBe("msg-resumed");
        snapshot.IsPartial.ShouldBeFalse(); // Should be complete after successful resume

        // Verify activator was called
        sessionActivator.ActivateAsyncCalls.Count.ShouldBe(1);
        sessionActivator.ActivateAsyncCalls[0].SessionId.ShouldBe(sessionId);

        // Verify fallback was NOT called
        fallbackSnapshotBuilder.BuildAsyncCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetSnapshotAsync_falls_back_when_resume_fails()
    {
        // Arrange
        var sessionId = "test-session-resume-2";
        var instanceId = "instance-resume-2";
        var session = new Session
        {
            Id = sessionId,
            InstanceId = instanceId,
            HarnessType = "opencode",
            HarnessResumeToken = "resume-token-456",
            Title = "Test Session",
            Status = "active",
            UserId = "user-1",
        };

        var sessionRepository = new InMemorySessionRepository();
        sessionRepository.Seed(session);

        var instanceTracker = new InstanceTracker();
        var activityTracker = new SessionActivityTracker();
        var delegationRepository = new InMemoryDelegationRepository();

        var fallbackSnapshot = new SessionSnapshot
        {
            Session = new SessionSnapshotSession
            {
                Id = sessionId,
                Title = "Test Session",
                Status = "active",
            },
            Messages =
            [
                new MessageLifecyclePayload
                {
                    Info = new MessageEventInfo
                    {
                        Id = "msg-fallback",
                        Role = "user",
                        SessionId = sessionId,
                        Time = new MessageEventTime { Created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                    },
                    Parts = [new TextMessageEventPart
                    {
                        Id = "part-1",
                        SessionId = sessionId,
                        MessageId = "msg-fallback",
                        Text = "Fallback message",
                    }],
                }
            ],
            ActivityStatus = "idle",
            HasMore = false,
            IsPartial = false,
        };

        var fallbackSnapshotBuilder = new FakeSessionSnapshotBuilder
        {
            BuildBehavior = (sid, pageSize, cursor) => Task.FromResult(fallbackSnapshot)
        };

        var sessionActivator = new FakeSessionActivator
        {
            ActivateBehavior = (sid, ct) =>
            {
                // Simulate resume failure
                return Task.FromResult(Result.Failure<IHarnessSession>(FleetError.NotFoundFor("Instance", instanceId)));
            }
        };
        var serviceProvider = CreateServiceProvider(sessionActivator);

        var proxy = new OpenCodeSessionMessageProxy(
            sessionRepository,
            instanceTracker,
            activityTracker,
            delegationRepository,
            fallbackSnapshotBuilder,
            serviceProvider,
            NullLogger<OpenCodeSessionMessageProxy>.Instance);

        // Act
        var snapshot = await proxy.GetSnapshotAsync(sessionId);

        // Assert
        snapshot.ShouldNotBeNull();
        snapshot.Messages.Count.ShouldBe(1);
        snapshot.Messages[0].Info.Id.ShouldBe("msg-fallback");
        snapshot.IsPartial.ShouldBeTrue(); // Should be partial after failed resume

        // Verify activator was called
        sessionActivator.ActivateAsyncCalls.Count.ShouldBe(1);
        sessionActivator.ActivateAsyncCalls[0].SessionId.ShouldBe(sessionId);

        // Verify fallback WAS called
        fallbackSnapshotBuilder.BuildAsyncCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetMessagesAsync_attempts_resume_when_harness_missing_but_resume_token_exists()
    {
        // Arrange
        var sessionId = "test-session-resume-3";
        var instanceId = "instance-resume-3";
        var session = new Session
        {
            Id = sessionId,
            InstanceId = instanceId,
            HarnessType = "opencode",
            HarnessResumeToken = "resume-token-789",
            Title = "Test Session",
            Status = "active",
            UserId = "user-1",
        };

        var sessionRepository = new InMemorySessionRepository();
        sessionRepository.Seed(session);

        var harnessSession = new FakeHarnessSession(instanceId)
        {
            GetMessagesBehavior = (query, ct) => Task.FromResult(new MessagePage(
                [
                    new HarnessMessage
                    {
                        Id = "msg-resumed",
                        Role = "assistant",
                        Parts = [new TextPart("Resumed response")],
                        Timestamp = DateTimeOffset.UtcNow,
                    }
                ],
                false))
        };

        var instanceTracker = new InstanceTracker();
        var activityTracker = new SessionActivityTracker();
        var delegationRepository = new InMemoryDelegationRepository();
        var fallbackSnapshotBuilder = new FakeSessionSnapshotBuilder();

        var sessionActivator = new FakeSessionActivator
        {
            ActivateBehavior = (sid, ct) =>
            {
                // Simulate successful resume
                instanceTracker.Register(instanceId, harnessSession);
                return Task.FromResult(Result.Success<IHarnessSession>(harnessSession));
            }
        };
        var serviceProvider = CreateServiceProvider(sessionActivator);

        var proxy = new OpenCodeSessionMessageProxy(
            sessionRepository,
            instanceTracker,
            activityTracker,
            delegationRepository,
            fallbackSnapshotBuilder,
            serviceProvider,
            NullLogger<OpenCodeSessionMessageProxy>.Instance);

        // Act
        var result = await proxy.GetMessagesAsync(sessionId);

        // Assert
        result.ShouldNotBeNull();
        result.Messages.Count.ShouldBe(1);
        result.Messages[0].Id.ShouldBe("msg-resumed");

        // Verify activator was called
        sessionActivator.ActivateAsyncCalls.Count.ShouldBe(1);

        // Verify fallback was NOT called
        fallbackSnapshotBuilder.BuildAsyncCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetSnapshotAsync_falls_back_when_resume_throws_exception()
    {
        // Arrange
        var sessionId = "test-session-resume-exception";
        var instanceId = "instance-resume-exception";
        var session = new Session
        {
            Id = sessionId,
            InstanceId = instanceId,
            HarnessType = "opencode",
            HarnessResumeToken = "resume-token-exception",
            Title = "Test Session",
            Status = "active",
            UserId = "user-1",
        };

        var sessionRepository = new InMemorySessionRepository();
        sessionRepository.Seed(session);

        var instanceTracker = new InstanceTracker();
        var activityTracker = new SessionActivityTracker();
        var delegationRepository = new InMemoryDelegationRepository();

        var fallbackSnapshot = new SessionSnapshot
        {
            Session = new SessionSnapshotSession
            {
                Id = sessionId,
                Title = "Test Session",
                Status = "active",
            },
            Messages =
            [
                new MessageLifecyclePayload
                {
                    Info = new MessageEventInfo
                    {
                        Id = "msg-fallback-exception",
                        Role = "user",
                        SessionId = sessionId,
                        Time = new MessageEventTime { Created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                    },
                    Parts = [new TextMessageEventPart
                    {
                        Id = "part-1",
                        SessionId = sessionId,
                        MessageId = "msg-fallback-exception",
                        Text = "Fallback after exception",
                    }],
                }
            ],
            ActivityStatus = "idle",
            HasMore = false,
            IsPartial = false,
        };

        var fallbackSnapshotBuilder = new FakeSessionSnapshotBuilder
        {
            BuildBehavior = (sid, pageSize, cursor) => Task.FromResult(fallbackSnapshot)
        };

        var sessionActivator = new FakeSessionActivator
        {
            ActivateBehavior = (sid, ct) =>
            {
                // Simulate resume throwing an exception
                throw new InvalidOperationException("Resume service unavailable");
            }
        };
        var serviceProvider = CreateServiceProvider(sessionActivator);

        var proxy = new OpenCodeSessionMessageProxy(
            sessionRepository,
            instanceTracker,
            activityTracker,
            delegationRepository,
            fallbackSnapshotBuilder,
            serviceProvider,
            NullLogger<OpenCodeSessionMessageProxy>.Instance);

        // Act
        var snapshot = await proxy.GetSnapshotAsync(sessionId);

        // Assert
        snapshot.ShouldNotBeNull();
        snapshot.Messages.Count.ShouldBe(1);
        snapshot.Messages[0].Info.Id.ShouldBe("msg-fallback-exception");
        snapshot.IsPartial.ShouldBeTrue(); // Should be partial after exception

        // Verify activator was called
        sessionActivator.ActivateAsyncCalls.Count.ShouldBe(1);
        sessionActivator.ActivateAsyncCalls[0].SessionId.ShouldBe(sessionId);

        // Verify fallback WAS called
        fallbackSnapshotBuilder.BuildAsyncCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetSnapshotAsync_skips_resume_when_no_resume_token()
    {
        // Arrange
        var sessionId = "test-session-no-token";
        var instanceId = "instance-no-token";
        var session = new Session
        {
            Id = sessionId,
            InstanceId = instanceId,
            HarnessType = "opencode",
            HarnessResumeToken = null, // No resume token
            Title = "Test Session",
            Status = "active",
            UserId = "user-1",
        };

        var sessionRepository = new InMemorySessionRepository();
        sessionRepository.Seed(session);

        var instanceTracker = new InstanceTracker();
        var activityTracker = new SessionActivityTracker();
        var delegationRepository = new InMemoryDelegationRepository();

        var fallbackSnapshot = new SessionSnapshot
        {
            Session = new SessionSnapshotSession
            {
                Id = sessionId,
                Title = "Test Session",
                Status = "active",
            },
            Messages = [],
            ActivityStatus = "idle",
            HasMore = false,
            IsPartial = false,
        };

        var fallbackSnapshotBuilder = new FakeSessionSnapshotBuilder
        {
            BuildBehavior = (sid, pageSize, cursor) => Task.FromResult(fallbackSnapshot)
        };

        var sessionActivator = new FakeSessionActivator();
        var serviceProvider = CreateServiceProvider(sessionActivator);

        var proxy = new OpenCodeSessionMessageProxy(
            sessionRepository,
            instanceTracker,
            activityTracker,
            delegationRepository,
            fallbackSnapshotBuilder,
            serviceProvider,
            NullLogger<OpenCodeSessionMessageProxy>.Instance);

        // Act
        var snapshot = await proxy.GetSnapshotAsync(sessionId);

        // Assert
        snapshot.ShouldNotBeNull();
        snapshot.IsPartial.ShouldBeTrue();

        // Verify activator was NOT called (no resume token)
        sessionActivator.ActivateAsyncCalls.ShouldBeEmpty();

        // Verify fallback WAS called
        fallbackSnapshotBuilder.BuildAsyncCalls.Count.ShouldBe(1);
    }
}
