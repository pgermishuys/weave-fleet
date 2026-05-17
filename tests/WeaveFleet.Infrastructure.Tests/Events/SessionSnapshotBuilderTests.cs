using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Infrastructure.Data.Repositories;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Tests.Data;

namespace WeaveFleet.Infrastructure.Tests.Events;

public sealed class SessionSnapshotBuilderTests
{
    [Fact]
    public async Task Should_build_snapshot_for_empty_session()
    {
        var (keeper, builder, factory, tracker) = await CreateAsync();
        using var _ = keeper;

        var sessionId = await SeedSessionAsync(factory);
        tracker.Update(sessionId, "working", TestUserContext.DefaultUserId);

        var snapshot = await builder.BuildAsync(sessionId);

        snapshot.Session.Id.ShouldBe(sessionId);
        snapshot.Messages.ShouldBeEmpty();
        snapshot.Delegations.ShouldBeEmpty();
        snapshot.ActivityStatus.ShouldBe("busy");
        snapshot.LastSequenceNumber.ShouldBeNull();
        snapshot.HasMore.ShouldBeFalse();
        snapshot.Cursor.ShouldBeNull();
    }

    [Fact]
    public async Task Should_build_snapshot_for_small_session()
    {
        var (keeper, builder, factory, tracker) = await CreateAsync();
        using var _ = keeper;

        var sessionId = await SeedSessionAsync(factory);
        await SeedMessagesAsync(factory, sessionId, 50);
        await SeedDelegationsAsync(factory, sessionId);
        await SeedHarnessEventsAsync(factory, sessionId, 7, 12);
        tracker.Update(sessionId, "busy", TestUserContext.DefaultUserId);

        var snapshot = await builder.BuildAsync(sessionId);

        snapshot.Messages.Count.ShouldBe(50);
        snapshot.Messages[0].Info.Id.ShouldBe("msg-00000");
        snapshot.Messages[^1].Info.Id.ShouldBe("msg-00049");
        snapshot.Messages[0].Info.Role.ShouldBe("assistant");
        snapshot.Messages[0].Info.Cost.ShouldBe(1.5d);
        snapshot.Messages[0].Info.Tokens.ShouldNotBeNull();
        var tokens = snapshot.Messages[0].Info.Tokens!;
        tokens.Input.ShouldBe(10d);
        tokens.Output.ShouldBe(5d);
        tokens.Reasoning.ShouldBe(2d);
        snapshot.Messages[0].Info.Time.Completed.ShouldNotBeNull();
        snapshot.Messages[0].Parts.Count.ShouldBe(2);
        snapshot.Messages[0].Parts[0].ShouldBeOfType<TextMessageEventPart>();
        snapshot.Messages[0].Parts[1].ShouldBeOfType<StepFinishedMessageEventPart>();
        snapshot.Delegations.Count.ShouldBe(2);
        snapshot.Delegations[0].DelegationId.ShouldBe("delegation-1");
        snapshot.Delegations[1].DelegationId.ShouldBe("delegation-2");
        snapshot.ActivityStatus.ShouldBe("busy");
        snapshot.LastSequenceNumber.ShouldBe(12);
        snapshot.HasMore.ShouldBeFalse();
        snapshot.Cursor.ShouldBeNull();
    }

    [Fact]
    public async Task Should_return_tail_page_for_large_session()
    {
        var (keeper, builder, factory, _) = await CreateAsync();
        using var _ = keeper;

        var sessionId = await SeedSessionAsync(factory);
        await SeedMessagesAsync(factory, sessionId, 10_000, includeStepFinish: false);
        await SeedHarnessEventsAsync(factory, sessionId, 100);

        var snapshot = await builder.BuildAsync(sessionId);

        snapshot.Messages.Count.ShouldBe(100);
        snapshot.Messages[0].Info.Id.ShouldBe("msg-09900");
        snapshot.Messages[^1].Info.Id.ShouldBe("msg-09999");
        snapshot.HasMore.ShouldBeTrue();
        snapshot.Cursor.ShouldNotBeNull();
        snapshot.LastSequenceNumber.ShouldBe(100);
    }

    [Fact]
    public async Task Should_continue_from_cursor_to_older_messages()
    {
        var (keeper, builder, factory, _) = await CreateAsync();
        using var _ = keeper;

        var sessionId = await SeedSessionAsync(factory);
        await SeedMessagesAsync(factory, sessionId, 250, includeStepFinish: false);

        var firstPage = await builder.BuildAsync(sessionId);
        var secondPage = await builder.BuildAsync(sessionId, cursor: firstPage.Cursor);
        var thirdPage = await builder.BuildAsync(sessionId, cursor: secondPage.Cursor);

        firstPage.Messages.Count.ShouldBe(100);
        firstPage.Messages[0].Info.Id.ShouldBe("msg-00150");
        firstPage.Messages[^1].Info.Id.ShouldBe("msg-00249");
        firstPage.HasMore.ShouldBeTrue();
        firstPage.Cursor.ShouldNotBeNull();

        secondPage.Messages.Count.ShouldBe(100);
        secondPage.Messages[0].Info.Id.ShouldBe("msg-00050");
        secondPage.Messages[^1].Info.Id.ShouldBe("msg-00149");
        secondPage.HasMore.ShouldBeTrue();
        secondPage.Cursor.ShouldNotBeNull();

        thirdPage.Messages.Count.ShouldBe(50);
        thirdPage.Messages[0].Info.Id.ShouldBe("msg-00000");
        thirdPage.Messages[^1].Info.Id.ShouldBe("msg-00049");
        thirdPage.HasMore.ShouldBeFalse();
        thirdPage.Cursor.ShouldBeNull();
    }

    private static async Task<(SqliteConnection Keeper, SessionSnapshotBuilder Builder, IDbConnectionFactory Factory, SessionActivityTracker Tracker)> CreateAsync()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        var tracker = new SessionActivityTracker();
        var builder = new SessionSnapshotBuilder(factory, new TestUserContext(), tracker);
        return (keeper, builder, factory, tracker);
    }

    private static async Task<string> SeedSessionAsync(IDbConnectionFactory factory)
        => (await WeaveFleet.Infrastructure.Tests.Data.Repositories.RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(factory, TestUserContext.DefaultUserId)).Session.Id;

    private static async Task SeedDelegationsAsync(IDbConnectionFactory factory, string sessionId)
    {
        var repository = new DelegationRepository(factory, new TestUserContext());
        await repository.InsertAsync(new Delegation
        {
            Id = "delegation-1",
            ParentSessionId = sessionId,
            ParentToolCallId = "tool-1",
            ChildSessionId = null,
            Title = "First delegation",
            Status = "running",
            CreatedAt = "2026-01-01T00:00:00.0000000+00:00",
            UpdatedAt = "2026-01-01T00:00:00.0000000+00:00",
        });
        await repository.InsertAsync(new Delegation
        {
            Id = "delegation-2",
            ParentSessionId = sessionId,
            ParentToolCallId = "tool-2",
            ChildSessionId = null,
            Title = "Second delegation",
            Status = "completed",
            CreatedAt = "2026-01-01T00:01:00.0000000+00:00",
            UpdatedAt = "2026-01-01T00:02:00.0000000+00:00",
            CompletedAt = "2026-01-01T00:02:00.0000000+00:00",
        });
    }

    private static async Task SeedHarnessEventsAsync(IDbConnectionFactory factory, string sessionId, params long[] sequenceNumbers)
    {
        var repository = new HarnessEventLogRepository(factory, new TestUserContext());
        foreach (var sequenceNumber in sequenceNumbers)
        {
            await repository.AppendAsync(new HarnessEventLogEntry
            {
                SessionId = sessionId,
                SequenceNumber = sequenceNumber,
                Type = "message.updated",
                Payload = "{}",
                UserId = TestUserContext.DefaultUserId,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
        }
    }

    private static async Task SeedMessagesAsync(IDbConnectionFactory factory, string sessionId, int count, bool includeStepFinish = true)
    {
        var baseTimestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rows = Enumerable.Range(0, count)
            .Select(index => new
            {
                Id = $"msg-{index:D5}",
                SessionId = sessionId,
                Role = "assistant",
                PartsJson = BuildPartsJson(index, includeStepFinish),
                Timestamp = baseTimestamp.AddSeconds(index).ToString("O"),
                CreatedAt = baseTimestamp.AddSeconds(index).AddHours(1).ToString("O"),
                AgentName = "loom",
                ModelId = "claude-sonnet-4",
            })
            .ToArray();

        using var connection = factory.CreateConnection();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync(
            """
            INSERT INTO messages (id, session_id, role, parts_json, timestamp, created_at, agent_name, model_id)
            VALUES (@Id, @SessionId, @Role, @PartsJson, @Timestamp, @CreatedAt, @AgentName, @ModelId)
            """,
            rows,
            transaction);
        transaction.Commit();
    }

    private static string BuildPartsJson(int index, bool includeStepFinish)
    {
        var parts = new List<object>
        {
            new
            {
                type = "text",
                text = $"message {index}"
            }
        };

        if (includeStepFinish)
        {
            parts.Add(new
            {
                type = "step-finish",
                index = 0,
                reason = "completed",
                cost = 1.5,
                tokensInput = 10,
                tokensOutput = 5,
                tokensReasoning = 2,
                completedAt = 1_704_067_200_000L + index
            });
        }

        return JsonSerializer.Serialize(parts);
    }
}
