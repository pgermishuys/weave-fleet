using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace NuCode.Sessions;

/// <summary>
/// SQLite-backed implementation of <see cref="ISessionStore"/>.
/// Uses WAL mode and JSON columns for message/part data.
/// Schema is auto-migrated on first use.
/// </summary>
internal sealed class SqliteSessionStore : ISessionStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public SqliteSessionStore(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        ApplyPragmas();
        EnsureSchema();
    }

    public SqliteSessionStore(SqliteConnection connection)
    {
        _connection = connection;
        if (_connection.State != System.Data.ConnectionState.Open)
        {
            _connection.Open();
        }
        ApplyPragmas();
        EnsureSchema();
    }

    public void Dispose() => _connection.Dispose();

    // ── Session CRUD ──

    public Task<NuCodeSession> CreateSessionAsync(NuCodeSession session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO session (id, slug, directory, title, version, parent_id, share_url,
                summary_additions, summary_deletions, summary_files, summary_diffs,
                revert, permission, time_created, time_updated, time_compacting, time_archived)
            VALUES (@id, @slug, @dir, @title, @version, @parentId, @shareUrl,
                @sumAdd, @sumDel, @sumFiles, @sumDiffs,
                @revert, @permission, @created, @updated, @compacting, @archived)
            """;

        BindSessionParams(cmd, session);
        cmd.ExecuteNonQuery();
        return Task.FromResult(session);
    }

    public Task<NuCodeSession?> GetSessionAsync(SessionId id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM session WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id.Value);

        using var reader = cmd.ExecuteReader();
        return Task.FromResult(reader.Read() ? ReadSession(reader) : null);
    }

    public Task<IReadOnlyList<NuCodeSession>> ListSessionsAsync(SessionFilter filter, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        var conditions = new List<string>();

        if (filter.Directory is not null)
        {
            conditions.Add("directory = @dir");
            cmd.Parameters.AddWithValue("@dir", filter.Directory);
        }

        if (filter.RootsOnly)
        {
            conditions.Add("parent_id IS NULL");
        }

        if (filter.UpdatedAfter.HasValue)
        {
            conditions.Add("time_updated > @updatedAfter");
            cmd.Parameters.AddWithValue("@updatedAfter", filter.UpdatedAfter.Value.ToUnixTimeMilliseconds());
        }

        if (filter.Search is not null)
        {
            conditions.Add("title LIKE @search");
            cmd.Parameters.AddWithValue("@search", $"%{filter.Search}%");
        }

        if (filter.ExcludeArchived)
        {
            conditions.Add("time_archived IS NULL");
        }

        var where = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : "";
        var limit = filter.Limit.HasValue ? $"LIMIT {filter.Limit.Value}" : "";
        cmd.CommandText = $"SELECT * FROM session {where} ORDER BY time_updated DESC {limit}";

        var results = new List<NuCodeSession>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadSession(reader));
        }

        return Task.FromResult<IReadOnlyList<NuCodeSession>>(results);
    }

    public Task<NuCodeSession> UpdateSessionAsync(NuCodeSession session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE session SET slug = @slug, directory = @dir, title = @title, version = @version,
                parent_id = @parentId, share_url = @shareUrl,
                summary_additions = @sumAdd, summary_deletions = @sumDel,
                summary_files = @sumFiles, summary_diffs = @sumDiffs,
                revert = @revert, permission = @permission,
                time_updated = @updated, time_compacting = @compacting, time_archived = @archived
            WHERE id = @id
            """;

        BindSessionParams(cmd, session);
        cmd.ExecuteNonQuery();
        return Task.FromResult(session);
    }

    public Task DeleteSessionAsync(SessionId id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM session WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id.Value);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NuCodeSession>> GetChildSessionsAsync(SessionId parentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM session WHERE parent_id = @parentId ORDER BY time_updated DESC";
        cmd.Parameters.AddWithValue("@parentId", parentId.Value);

        var results = new List<NuCodeSession>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadSession(reader));
        }

        return Task.FromResult<IReadOnlyList<NuCodeSession>>(results);
    }

    // ── Message CRUD ──

    public Task<NuCodeMessage> UpsertMessageAsync(NuCodeMessage message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var data = JsonSerializer.Serialize(message, message.GetType(), JsonOptions);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO message (id, session_id, time_created, time_updated, data)
            VALUES (@id, @sessionId, @created, @updated, @data)
            ON CONFLICT(id) DO UPDATE SET data = @data, time_updated = @updated
            """;

        cmd.Parameters.AddWithValue("@id", message.Id.Value);
        cmd.Parameters.AddWithValue("@sessionId", message.SessionId.Value);
        cmd.Parameters.AddWithValue("@created", message.CreatedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("@updated", now);
        cmd.Parameters.AddWithValue("@data", data);
        cmd.ExecuteNonQuery();

        return Task.FromResult(message);
    }

    public Task<IReadOnlyList<MessageWithParts>> GetMessagesAsync(SessionId sessionId, int? limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Get messages
        var messages = new List<NuCodeMessage>();
        using (var cmd = _connection.CreateCommand())
        {
            var limitClause = limit.HasValue ? $"LIMIT {limit.Value}" : "";
            cmd.CommandText = $"SELECT id, data FROM message WHERE session_id = @sessionId ORDER BY time_created ASC, id ASC {limitClause}";
            cmd.Parameters.AddWithValue("@sessionId", sessionId.Value);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var data = reader.GetString(1);
                var msg = DeserializeMessage(data);
                if (msg is not null)
                {
                    messages.Add(msg);
                }
            }
        }

        // Get all parts for the session
        var partsByMessage = new Dictionary<string, List<MessagePart>>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT message_id, data FROM part WHERE session_id = @sessionId ORDER BY id ASC";
            cmd.Parameters.AddWithValue("@sessionId", sessionId.Value);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var messageId = reader.GetString(0);
                var data = reader.GetString(1);
                var part = DeserializePart(data);
                if (part is not null)
                {
                    if (!partsByMessage.TryGetValue(messageId, out var list))
                    {
                        list = [];
                        partsByMessage[messageId] = list;
                    }
                    list.Add(part);
                }
            }
        }

        var results = messages.Select(m =>
        {
            var parts = partsByMessage.TryGetValue(m.Id.Value, out var list)
                ? (IReadOnlyList<MessagePart>)list
                : Array.Empty<MessagePart>();
            return new MessageWithParts(m, parts);
        }).ToList();

        return Task.FromResult<IReadOnlyList<MessageWithParts>>(results);
    }

    public Task DeleteMessageAsync(SessionId sessionId, MessageId messageId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM message WHERE id = @id AND session_id = @sessionId";
        cmd.Parameters.AddWithValue("@id", messageId.Value);
        cmd.Parameters.AddWithValue("@sessionId", sessionId.Value);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    // ── Part CRUD ──

    public Task<MessagePart> UpsertPartAsync(MessagePart part, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var data = JsonSerializer.Serialize(part, part.GetType(), JsonOptions);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO part (id, message_id, session_id, time_created, time_updated, data)
            VALUES (@id, @messageId, @sessionId, @created, @updated, @data)
            ON CONFLICT(id) DO UPDATE SET data = @data, time_updated = @updated
            """;

        cmd.Parameters.AddWithValue("@id", part.Id.Value);
        cmd.Parameters.AddWithValue("@messageId", part.MessageId.Value);
        cmd.Parameters.AddWithValue("@sessionId", part.SessionId.Value);
        cmd.Parameters.AddWithValue("@created", now);
        cmd.Parameters.AddWithValue("@updated", now);
        cmd.Parameters.AddWithValue("@data", data);
        cmd.ExecuteNonQuery();

        return Task.FromResult(part);
    }

    public Task DeletePartAsync(SessionId sessionId, MessageId messageId, PartId partId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM part WHERE id = @id AND session_id = @sessionId";
        cmd.Parameters.AddWithValue("@id", partId.Value);
        cmd.Parameters.AddWithValue("@sessionId", sessionId.Value);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MessagePart>> GetPartsAsync(MessageId messageId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var results = new List<MessagePart>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT data FROM part WHERE message_id = @messageId ORDER BY id ASC";
        cmd.Parameters.AddWithValue("@messageId", messageId.Value);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var data = reader.GetString(0);
            var part = DeserializePart(data);
            if (part is not null)
            {
                results.Add(part);
            }
        }

        return Task.FromResult<IReadOnlyList<MessagePart>>(results);
    }

    // ── Schema ──

    private void ApplyPragmas()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA cache_size = -64000;
            PRAGMA foreign_keys = ON;
            """;
        cmd.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS session (
                id TEXT PRIMARY KEY,
                slug TEXT NOT NULL,
                directory TEXT NOT NULL,
                title TEXT NOT NULL,
                version TEXT NOT NULL,
                parent_id TEXT,
                share_url TEXT,
                summary_additions INTEGER,
                summary_deletions INTEGER,
                summary_files INTEGER,
                summary_diffs TEXT,
                revert TEXT,
                permission TEXT,
                time_created INTEGER NOT NULL,
                time_updated INTEGER NOT NULL,
                time_compacting INTEGER,
                time_archived INTEGER
            );

            CREATE INDEX IF NOT EXISTS session_parent_idx ON session(parent_id);

            CREATE TABLE IF NOT EXISTS message (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL,
                time_updated INTEGER NOT NULL,
                data TEXT NOT NULL,
                FOREIGN KEY(session_id) REFERENCES session(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS message_session_time_idx
                ON message(session_id, time_created, id);

            CREATE TABLE IF NOT EXISTS part (
                id TEXT PRIMARY KEY,
                message_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL,
                time_updated INTEGER NOT NULL,
                data TEXT NOT NULL,
                FOREIGN KEY(message_id) REFERENCES message(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS part_message_idx ON part(message_id, id);
            CREATE INDEX IF NOT EXISTS part_session_idx ON part(session_id);
            """;
        cmd.ExecuteNonQuery();
    }

    // ── Parameter binding ──

    private static void BindSessionParams(SqliteCommand cmd, NuCodeSession session)
    {
        cmd.Parameters.AddWithValue("@id", session.Id.Value);
        cmd.Parameters.AddWithValue("@slug", session.Slug);
        cmd.Parameters.AddWithValue("@dir", session.Directory);
        cmd.Parameters.AddWithValue("@title", session.Title);
        cmd.Parameters.AddWithValue("@version", session.Version);
        cmd.Parameters.AddWithValue("@parentId", (object?)session.ParentId?.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@shareUrl", (object?)session.ShareUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sumAdd", (object?)session.Summary?.Additions ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sumDel", (object?)session.Summary?.Deletions ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sumFiles", (object?)session.Summary?.Files ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sumDiffs",
            session.Summary?.Diffs is { } diffs
                ? JsonSerializer.Serialize(diffs, JsonOptions)
                : DBNull.Value);
        cmd.Parameters.AddWithValue("@revert",
            session.Revert is { } revert
                ? JsonSerializer.Serialize(revert, JsonOptions)
                : DBNull.Value);
        cmd.Parameters.AddWithValue("@permission",
            session.Permissions is { } perm
                ? JsonSerializer.Serialize(perm, JsonOptions)
                : DBNull.Value);
        cmd.Parameters.AddWithValue("@created", session.CreatedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("@updated", session.UpdatedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("@compacting", session.CompactingAt.HasValue
            ? session.CompactingAt.Value.ToUnixTimeMilliseconds()
            : DBNull.Value);
        cmd.Parameters.AddWithValue("@archived", session.ArchivedAt.HasValue
            ? session.ArchivedAt.Value.ToUnixTimeMilliseconds()
            : DBNull.Value);
    }

    // ── Reading ──

    private static NuCodeSession ReadSession(SqliteDataReader reader)
    {
        var summaryAdd = reader.IsDBNull(reader.GetOrdinal("summary_additions")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("summary_additions"));
        var summaryDel = reader.IsDBNull(reader.GetOrdinal("summary_deletions")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("summary_deletions"));
        var summaryFiles = reader.IsDBNull(reader.GetOrdinal("summary_files")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("summary_files"));

        SessionSummary? summary = null;
        if (summaryAdd.HasValue)
        {
            ImmutableArray<FileDiff>? diffs = null;
            var diffsOrd = reader.GetOrdinal("summary_diffs");
            if (!reader.IsDBNull(diffsOrd))
            {
                diffs = JsonSerializer.Deserialize<ImmutableArray<FileDiff>>(reader.GetString(diffsOrd), JsonOptions);
            }
            summary = new SessionSummary(summaryAdd.Value, summaryDel ?? 0, summaryFiles ?? 0, diffs);
        }

        SessionRevert? revert = null;
        var revertOrd = reader.GetOrdinal("revert");
        if (!reader.IsDBNull(revertOrd))
        {
            revert = JsonSerializer.Deserialize<SessionRevert>(reader.GetString(revertOrd), JsonOptions);
        }

        Permissions.PermissionRuleset? permission = null;
        var permOrd = reader.GetOrdinal("permission");
        if (!reader.IsDBNull(permOrd))
        {
            permission = JsonSerializer.Deserialize<Permissions.PermissionRuleset>(reader.GetString(permOrd), JsonOptions);
        }

        var parentOrd = reader.GetOrdinal("parent_id");
        var shareOrd = reader.GetOrdinal("share_url");
        var compactOrd = reader.GetOrdinal("time_compacting");
        var archiveOrd = reader.GetOrdinal("time_archived");

        return new NuCodeSession
        {
            Id = new SessionId(reader.GetString(reader.GetOrdinal("id"))),
            Slug = reader.GetString(reader.GetOrdinal("slug")),
            Directory = reader.GetString(reader.GetOrdinal("directory")),
            Title = reader.GetString(reader.GetOrdinal("title")),
            Version = reader.GetString(reader.GetOrdinal("version")),
            ParentId = reader.IsDBNull(parentOrd) ? null : new SessionId(reader.GetString(parentOrd)),
            ShareUrl = reader.IsDBNull(shareOrd) ? null : reader.GetString(shareOrd),
            Summary = summary,
            Revert = revert,
            Permissions = permission,
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("time_created"))),
            UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("time_updated"))),
            CompactingAt = reader.IsDBNull(compactOrd) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(compactOrd)),
            ArchivedAt = reader.IsDBNull(archiveOrd) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(archiveOrd)),
        };
    }

    // ── JSON serialization ──

    private static NuCodeMessage? DeserializeMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var role = doc.RootElement.GetProperty("role").GetString();
        return role switch
        {
            "user" => JsonSerializer.Deserialize<UserMessage>(json, JsonOptions),
            "assistant" => JsonSerializer.Deserialize<AssistantMessage>(json, JsonOptions),
            _ => null,
        };
    }

    private static MessagePart? DeserializePart(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var type = doc.RootElement.GetProperty("type").GetString();
        return type switch
        {
            "text" => JsonSerializer.Deserialize<TextPart>(json, JsonOptions),
            "reasoning" => JsonSerializer.Deserialize<ReasoningPart>(json, JsonOptions),
            "tool" => JsonSerializer.Deserialize<ToolPart>(json, JsonOptions),
            "file" => JsonSerializer.Deserialize<FilePart>(json, JsonOptions),
            "snapshot" => JsonSerializer.Deserialize<SnapshotPart>(json, JsonOptions),
            "patch" => JsonSerializer.Deserialize<PatchPart>(json, JsonOptions),
            "agent" => JsonSerializer.Deserialize<AgentPart>(json, JsonOptions),
            "compaction" => JsonSerializer.Deserialize<CompactionPart>(json, JsonOptions),
            "subtask" => JsonSerializer.Deserialize<SubtaskPart>(json, JsonOptions),
            "retry" => JsonSerializer.Deserialize<RetryPart>(json, JsonOptions),
            "step-start" => JsonSerializer.Deserialize<StepStartPart>(json, JsonOptions),
            "step-finish" => JsonSerializer.Deserialize<StepFinishPart>(json, JsonOptions),
            _ => null,
        };
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new SessionIdJsonConverter());
        options.Converters.Add(new MessageIdJsonConverter());
        options.Converters.Add(new PartIdJsonConverter());
        options.Converters.Add(new ToolCallStateJsonConverter());
        options.Converters.Add(new MessageErrorJsonConverter());
        return options;
    }

    // ── JSON converters for strong-typed IDs ──

    private sealed class SessionIdJsonConverter : JsonConverter<SessionId>
    {
        public override SessionId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, SessionId value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class MessageIdJsonConverter : JsonConverter<MessageId>
    {
        public override MessageId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, MessageId value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class PartIdJsonConverter : JsonConverter<PartId>
    {
        public override PartId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, PartId value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    /// <summary>
    /// Handles polymorphic serialization/deserialization of <see cref="ToolCallState"/>.
    /// </summary>
    private sealed class ToolCallStateJsonConverter : JsonConverter<ToolCallState>
    {
        public override ToolCallState? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var status = doc.RootElement.GetProperty("status").GetString();
            var raw = doc.RootElement.GetRawText();
            return status switch
            {
                "pending" => JsonSerializer.Deserialize<PendingToolCallState>(raw, options),
                "running" => JsonSerializer.Deserialize<RunningToolCallState>(raw, options),
                "completed" => JsonSerializer.Deserialize<CompletedToolCallState>(raw, options),
                "error" => JsonSerializer.Deserialize<ErrorToolCallState>(raw, options),
                _ => null,
            };
        }

        public override void Write(Utf8JsonWriter writer, ToolCallState value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }

    /// <summary>
    /// Handles polymorphic serialization/deserialization of <see cref="MessageError"/>.
    /// </summary>
    private sealed class MessageErrorJsonConverter : JsonConverter<MessageError>
    {
        public override MessageError? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var name = doc.RootElement.GetProperty("name").GetString();
            var raw = doc.RootElement.GetRawText();
            return name switch
            {
                "ProviderAuthError" => JsonSerializer.Deserialize<ProviderAuthError>(raw, options),
                "MessageOutputLengthError" => JsonSerializer.Deserialize<OutputLengthError>(raw, options),
                "MessageAbortedError" => JsonSerializer.Deserialize<AbortedError>(raw, options),
                "ContextOverflowError" => JsonSerializer.Deserialize<ContextOverflowError>(raw, options),
                "APIError" => JsonSerializer.Deserialize<ApiError>(raw, options),
                "Unknown" => JsonSerializer.Deserialize<UnknownMessageError>(raw, options),
                _ => null,
            };
        }

        public override void Write(Utf8JsonWriter writer, MessageError value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
