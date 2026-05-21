using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using WeaveFleet.Application;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Data;

namespace WeaveFleet.Infrastructure.Events;

/// <summary>
/// SQLite-backed implementation of <see cref="ISessionSnapshotBuilder"/>.
/// </summary>
public sealed class SessionSnapshotBuilder(
    IDbConnectionFactory connectionFactory,
    IUserContext userContext,
    SessionActivityTracker activityTracker) : ISessionSnapshotBuilder
{
    private const string IdleStatus = "idle";
    private const string BusyStatus = "busy";

    /// <inheritdoc />
    public async Task<SessionSnapshot> BuildAsync(string sessionId, int pageSize = 100, string? cursor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        using var connection = connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);

        var session = await connection.QueryFirstOrDefaultAsync(
            """
            SELECT id, title, status
            FROM sessions
            WHERE id = @SessionId AND user_id = @UserId
            LIMIT 1
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            ReadSessionRow,
            transaction).ConfigureAwait(false);

        if (session is null)
            throw new InvalidOperationException($"Session '{sessionId}' was not found.");

        var beforeMessageId = DecodeCursor(cursor);
        string? cursorCreatedAt = null;

        if (beforeMessageId is not null)
        {
            cursorCreatedAt = await connection.ExecuteScalarAsync<string>(
                """
                SELECT m.created_at
                FROM messages m
                INNER JOIN sessions s ON s.id = m.session_id
                WHERE m.session_id = @SessionId
                  AND m.id = @MessageId
                  AND s.user_id = @UserId
                LIMIT 1
                """,
                cmd =>
                {
                    cmd.AddParameter("SessionId", sessionId);
                    cmd.AddParameter("MessageId", beforeMessageId);
                    cmd.AddParameter("UserId", userContext.UserId);
                },
                transaction).ConfigureAwait(false);

            if (cursorCreatedAt is null)
                throw new ArgumentException("Cursor is invalid or expired.", nameof(cursor));
        }

        var messageRows = await connection.QueryAsync(
            """
            SELECT m.id, m.session_id, m.role, m.parts_json, m.timestamp, m.created_at, m.agent_name, m.model_id
            FROM messages m
            INNER JOIN sessions s ON s.id = m.session_id
            WHERE m.session_id = @SessionId
              AND s.user_id = @UserId
              AND (
                    @CursorCreatedAt IS NULL
                    OR m.created_at < @CursorCreatedAt
                    OR (m.created_at = @CursorCreatedAt AND m.id < @CursorId)
                  )
            ORDER BY m.created_at DESC, m.id DESC
            LIMIT @Limit
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userContext.UserId);
                cmd.AddParameter("CursorCreatedAt", cursorCreatedAt);
                cmd.AddParameter("CursorId", beforeMessageId);
                cmd.AddParameter("Limit", checked(pageSize + 1));
            },
            ReadMessageRow,
            transaction).ConfigureAwait(false);

        var hasMore = messageRows.Count > pageSize;
        if (hasMore)
            messageRows.RemoveAt(messageRows.Count - 1);

        messageRows.Reverse();

        var delegationRows = await connection.QueryAsync(
            """
            SELECT d.id, d.parent_tool_call_id, d.child_session_id, d.title, d.status, d.created_at
            FROM delegations d
            INNER JOIN sessions s ON s.id = d.parent_session_id
            WHERE d.parent_session_id = @SessionId AND s.user_id = @UserId
            ORDER BY d.created_at ASC, d.id ASC
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            ReadDelegationRow,
            transaction).ConfigureAwait(false);

        var lastEventId = await connection.ExecuteScalarAsync<long?>(
            """
            SELECT MAX(event_id)
            FROM harness_events
            WHERE session_id = @SessionId
              AND (user_id = @UserId OR user_id IS NULL)
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            transaction).ConfigureAwait(false);

        transaction.Commit();

        return new SessionSnapshot
        {
            Session = new SessionSnapshotSession
            {
                Id = session.Id,
                Title = session.Title,
                Status = session.Status,
            },
            Messages = messageRows.Select(ToMessageLifecyclePayload).ToArray(),
            Delegations = delegationRows.Select(row => new SessionSnapshotDelegation
            {
                DelegationId = row.Id,
                ParentToolCallId = row.ParentToolCallId,
                ChildSessionId = row.ChildSessionId,
                Title = row.Title,
                Status = row.Status,
                CreatedAt = row.CreatedAt,
            }).ToArray(),
            ActivityStatus = NormalizeActivityStatus(activityTracker.GetEffectiveActivityStatus(sessionId)),
            LastEventId = lastEventId,
            HasMore = hasMore,
            Cursor = hasMore && messageRows.Count > 0 ? EncodeCursor(messageRows[0].Id) : null,
        };
    }

    private static MessageLifecyclePayload ToMessageLifecyclePayload(MessageRow message)
    {
        var persistedParts = JsonSerializer.Deserialize(message.PartsJson, ApplicationJsonContext.Default.ListMessagePart) ?? [];
        var parts = new List<MessageEventPart>(persistedParts.Count);
        double totalCost = 0;
        double totalInputTokens = 0;
        double totalOutputTokens = 0;
        double totalReasoningTokens = 0;
        long? completedAt = null;

        for (var index = 0; index < persistedParts.Count; index++)
        {
            switch (persistedParts[index])
            {
                case TextPart textPart:
                    parts.Add(new TextMessageEventPart
                    {
                        Id = $"{message.Id}-text-{index}",
                        SessionId = message.SessionId,
                        MessageId = message.Id,
                        Text = textPart.Text,
                    });
                    break;

                case ReasoningPart reasoningPart:
                    parts.Add(new ReasoningMessageEventPart
                    {
                        Id = $"{message.Id}-reasoning-{index}",
                        SessionId = message.SessionId,
                        MessageId = message.Id,
                        Text = reasoningPart.Text,
                        Summary = reasoningPart.Summary,
                    });
                    break;

                case ToolUsePart toolPart:
                    parts.Add(new ToolMessageEventPart
                    {
                        Id = $"{message.Id}-tool-{index}",
                        SessionId = message.SessionId,
                        MessageId = message.Id,
                        ToolName = toolPart.ToolName,
                        CallId = toolPart.ToolCallId,
                        State = ToToolInvocationState(toolPart),
                    });
                    break;

                case FilePart filePart:
                    parts.Add(new FileMessageEventPart
                    {
                        Id = string.IsNullOrWhiteSpace(filePart.PartId) ? $"{message.Id}-file-{index}" : filePart.PartId,
                        SessionId = message.SessionId,
                        MessageId = message.Id,
                        Mime = filePart.Mime,
                        Url = filePart.Url,
                        Filename = filePart.Filename,
                    });
                    break;

                case StepFinishPart stepFinishPart:
                    totalCost += stepFinishPart.Cost;
                    totalInputTokens += stepFinishPart.TokensInput;
                    totalOutputTokens += stepFinishPart.TokensOutput;
                    totalReasoningTokens += stepFinishPart.TokensReasoning;
                    completedAt = GetLatestCompletedAt(completedAt, stepFinishPart.CompletedAt);

                    parts.Add(new StepFinishedMessageEventPart
                    {
                        Id = $"{message.Id}-step-finish-{stepFinishPart.Index}",
                        SessionId = message.SessionId,
                        MessageId = message.Id,
                        Index = stepFinishPart.Index,
                        Reason = stepFinishPart.Reason,
                        Cost = stepFinishPart.Cost,
                        Tokens = new MessageTokenUsage
                        {
                            Input = stepFinishPart.TokensInput,
                            Output = stepFinishPart.TokensOutput,
                            Reasoning = stepFinishPart.TokensReasoning,
                        },
                        CompletedAt = stepFinishPart.CompletedAt,
                    });
                    break;
            }
        }

        return new MessageLifecyclePayload
        {
            Info = new MessageEventInfo
            {
                Id = message.Id,
                Role = message.Role,
                SessionId = message.SessionId,
                Agent = message.AgentName,
                ModelId = message.ModelId,
                Time = new MessageEventTime
                {
                    Created = ParseUnixTimeMilliseconds(message.CreatedAt),
                    Completed = completedAt,
                },
                Cost = totalCost > 0 ? totalCost : null,
                Tokens = totalInputTokens > 0 || totalOutputTokens > 0 || totalReasoningTokens > 0
                    ? new MessageTokenUsage
                    {
                        Input = totalInputTokens,
                        Output = totalOutputTokens,
                        Reasoning = totalReasoningTokens,
                    }
                    : null,
            },
            Parts = parts,
        };
    }

    private static ToolInvocationState ToToolInvocationState(ToolUsePart toolPart)
    {
        var input = toolPart.Arguments.ValueKind == JsonValueKind.Undefined
            ? (JsonElement?)null
            : toolPart.Arguments.Clone();

        return toolPart.State switch
        {
            ToolUseState.Pending => new ToolPendingState { Input = input },
            ToolUseState.Running => new ToolRunningState { Input = input },
            ToolUseState.Completed => new ToolCompletedState { Input = input },
            ToolUseState.Error => new ToolErrorState { Input = input },
            _ => new ToolPendingState { Input = input },
        };
    }

    private static long ParseUnixTimeMilliseconds(string timestamp)
        => DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUnixTimeMilliseconds();

    private static long? GetLatestCompletedAt(long? current, long? candidate)
    {
        if (candidate is null)
            return current;

        return current is null
            ? candidate
            : Math.Max(current.Value, candidate.Value);
    }

    private static string NormalizeActivityStatus(string? activityStatus)
        => string.Equals(activityStatus, BusyStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(activityStatus, "working", StringComparison.OrdinalIgnoreCase)
                ? BusyStatus
                : IdleStatus;

    private static string EncodeCursor(string messageId)
    {
        var bytes = Encoding.UTF8.GetBytes(messageId);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return null;

        try
        {
            var normalized = cursor.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Cursor is invalid.", nameof(cursor), ex);
        }
    }

    private static SessionRow ReadSessionRow(System.Data.Common.DbDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("id")),
        reader.GetString(reader.GetOrdinal("title")),
        reader.GetString(reader.GetOrdinal("status")));

    private static MessageRow ReadMessageRow(System.Data.Common.DbDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("id")),
        reader.GetString(reader.GetOrdinal("session_id")),
        reader.GetString(reader.GetOrdinal("role")),
        reader.GetString(reader.GetOrdinal("parts_json")),
        reader.GetString(reader.GetOrdinal("timestamp")),
        reader.GetString(reader.GetOrdinal("created_at")),
        reader.GetNullableString(reader.GetOrdinal("agent_name")),
        reader.GetNullableString(reader.GetOrdinal("model_id")));

    private static DelegationRow ReadDelegationRow(System.Data.Common.DbDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("id")),
        reader.GetNullableString(reader.GetOrdinal("parent_tool_call_id")),
        reader.GetNullableString(reader.GetOrdinal("child_session_id")),
        reader.GetString(reader.GetOrdinal("title")),
        reader.GetString(reader.GetOrdinal("status")),
        reader.GetString(reader.GetOrdinal("created_at")));

    private sealed record SessionRow(string Id, string Title, string Status);

    private sealed record MessageRow(
        string Id,
        string SessionId,
        string Role,
        string PartsJson,
        string Timestamp,
        string CreatedAt,
        string? AgentName,
        string? ModelId);

    private sealed record DelegationRow(
        string Id,
        string? ParentToolCallId,
        string? ChildSessionId,
        string Title,
        string Status,
        string CreatedAt);
}
