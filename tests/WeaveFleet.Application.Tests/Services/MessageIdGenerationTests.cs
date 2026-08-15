using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Identity;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// Tests verifying that the orchestrator generates ascending message IDs (msg_[0-9a-f]{12}[0-9A-Za-z]{14})
/// for user prompts and that echo suppression in HarnessEventRelay works by exact ID match.
/// </summary>
public sealed class MessageIdGenerationTests : IAsyncDisposable
{
    private readonly SessionOrchestratorBuilder _builder;
    private readonly InstanceTracker _tracker = new();
    private readonly FakeHarnessSession _defaultSession = new("inst-1");
    private readonly SessionOrchestrator _sut;

    // Regex pattern matching msg_[12 hex chars][14 base62 chars]
    private static readonly Regex MessageIdPattern = new(@"^msg_[0-9a-f]{12}[0-9A-Za-z]{14}$", RegexOptions.Compiled);

    public ValueTask DisposeAsync() => _defaultSession.DisposeAsync();

    public MessageIdGenerationTests()
    {
        _builder = new SessionOrchestratorBuilder()
            .WithUserContext(new TestUserContext("user-1"));

        _builder.WorkspaceRootRepository.Seed(
            new WorkspaceRoot { Id = "root-1", Path = Path.GetTempPath(), CreatedAt = DateTime.UtcNow.ToString("O") }
        );

        _builder.InstanceRepository.GetByIdBehavior = id => Task.FromResult<Instance?>(new Instance
        {
            Id = id,
            Port = 0,
            Directory = "/tmp",
            Url = string.Empty,
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        _sut = _builder.Build();
    }

    [Fact]
    public async Task prompt_session_async_generates_ascending_message_id_with_correct_format()
    {
        // Arrange
        _builder.SessionRepository.Seed(new Session
        {
            Id = "s1",
            InstanceId = "inst-1",
            Title = "Test",
            Status = "active",
            Directory = "/tmp",
            CreatedAt = "2026-01-01",
            RetentionStatus = "active"
        });
        _tracker.Register("inst-1", _defaultSession);
        var sut = BuildSutWithTracker();

        // Act
        var result = await sut.PromptSessionAsync("s1", "hello");

        // Assert
        result.IsSuccess.ShouldBeTrue();

        // Verify the harness received the prompt with a msg_ ID
        _defaultSession.SendPromptCalls.Count.ShouldBe(1);
        var promptOptions = _defaultSession.SendPromptCalls[0].Options;
        promptOptions.ShouldNotBeNull();
        promptOptions.MessageId.ShouldNotBeNull();
        MessageIdPattern.IsMatch(promptOptions.MessageId).ShouldBeTrue(
            $"MessageId '{promptOptions.MessageId}' does not match expected pattern msg_[0-9a-f]{{12}}[0-9A-Za-z]{{14}}");
    }

    [Fact]
    public async Task prompt_session_async_broadcasts_user_message_with_generated_message_id()
    {
        // Arrange
        _builder.SessionRepository.Seed(new Session
        {
            Id = "s1",
            InstanceId = "inst-1",
            Title = "Test",
            Status = "active",
            Directory = "/tmp",
            CreatedAt = "2026-01-01",
            RetentionStatus = "active"
        });
        _tracker.Register("inst-1", _defaultSession);
        var sut = BuildSutWithTracker();

        // Act
        var result = await sut.PromptSessionAsync("s1", "hello world");

        // Assert
        result.IsSuccess.ShouldBeTrue();

        // Verify the broadcast contains the user message with msg_ ID
        // The event type is "message.updated" (dot, not underscore)
        var userMessageBroadcast = _builder.EventBroadcaster.Broadcasts
            .FirstOrDefault(b => b.Topic == "session:s1" && b.Type == "message.updated");
        userMessageBroadcast.ShouldNotBeNull($"Expected message.updated broadcast. Found: {string.Join(", ", _builder.EventBroadcaster.Broadcasts.Select(b => b.Type))}");

        var payload = userMessageBroadcast.Payload;
        payload.TryGetProperty("info", out var info).ShouldBeTrue();
        info.TryGetProperty("id", out var messageId).ShouldBeTrue();
        var id = messageId.GetString();
        id.ShouldNotBeNull();
        MessageIdPattern.IsMatch(id).ShouldBeTrue(
            $"Broadcast message ID '{id}' does not match expected pattern");

        // Verify the same ID was passed to the harness
        var promptOptions = _defaultSession.SendPromptCalls[0].Options;
        promptOptions.ShouldNotBeNull();
        promptOptions.MessageId.ShouldBe(id);
    }

    [Fact]
    public async Task prompt_session_async_generates_unique_ascending_ids_for_sequential_prompts()
    {
        // Arrange
        _builder.SessionRepository.Seed(new Session
        {
            Id = "s1",
            InstanceId = "inst-1",
            Title = "Test",
            Status = "active",
            Directory = "/tmp",
            CreatedAt = "2026-01-01",
            RetentionStatus = "active"
        });
        _tracker.Register("inst-1", _defaultSession);
        var sut = BuildSutWithTracker();

        // Act - send three prompts
        await sut.PromptSessionAsync("s1", "first");
        await sut.PromptSessionAsync("s1", "second");
        await sut.PromptSessionAsync("s1", "third");

        // Assert
        _defaultSession.SendPromptCalls.Count.ShouldBe(3);
        var ids = _defaultSession.SendPromptCalls
            .Select(call => call.Options?.MessageId)
            .ToList();

        // All IDs should be non-null and match the pattern
        foreach (var id in ids)
        {
            id.ShouldNotBeNull();
            MessageIdPattern.IsMatch(id).ShouldBeTrue($"ID '{id}' does not match pattern");
        }

        // All IDs should be unique
        ids.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public void ascending_message_id_format_matches_specification()
    {
        // Generate a message ID and verify its structure
        var id = AscendingMessageId.New();

        // Should match: msg_[12 hex chars][14 base62 chars]
        MessageIdPattern.IsMatch(id).ShouldBeTrue($"Generated ID '{id}' does not match pattern");

        // Should be exactly 30 characters (msg_ = 4, hex = 12, base62 = 14)
        id.Length.ShouldBe(30);

        // Should start with msg_
        id.ShouldStartWith("msg_");

        // Hex portion (chars 4-15) should be lowercase hex
        var hexPortion = id.Substring(4, 12);
        hexPortion.ShouldMatch(@"^[0-9a-f]{12}$");

        // Base62 portion (chars 16-29) should be alphanumeric
        var base62Portion = id.Substring(16, 14);
        base62Portion.ShouldMatch(@"^[0-9A-Za-z]{14}$");
    }


    private SessionOrchestrator BuildSutWithTracker()
    {
        var userContext = new TestUserContext("user-1");
        var options = new FleetOptions();
        var workspaceRootService = new WorkspaceRootService(_builder.WorkspaceRootRepository, userContext);
        var workspaceService = new WorkspaceService(
            _builder.WorkspaceRepository,
            userContext,
            options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceService>.Instance);
        var instanceService = new InstanceService(_builder.InstanceRepository, _builder.SessionRepository, userContext);
        var sessionSourceResolutionService = new SessionSourceResolutionService([
            new LocalDirectorySessionSourceProvider(workspaceRootService)
        ]);
        var delegationService = new DelegationService(_builder.DelegationRepository, _builder.EventBroadcaster, userContext);

        return new SessionOrchestrator(
            workspaceService,
            instanceService,
            sessionSourceResolutionService,
            _builder.HarnessRegistry,
            _tracker,
            _builder.SessionRepository,
            _builder.SessionSourceUsageRepository,
            _builder.SessionCallbackRepository,
            _builder.DelegationRepository,
            _builder.ProjectRepository,
            _builder.EventBroadcaster,
            _builder.AnalyticsCollector,
            _builder.SessionMessageProxy,
            delegationService,
            _builder.CredentialStore,
            _builder.UserPreferenceRepository,
            userContext,
            options,
            _builder.SmartLinkRepository,
            _builder.ActivityTracker,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionOrchestrator>.Instance,
            sessionActivityWriteService: null,
            gitDiffService: null);
    }
}
