using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

/// <summary>
/// In-memory <see cref="ICanvasRepository"/> with the same rules as the SQLite one: the version guard on
/// updates, <c>agent_seen_version</c> never moving backwards, and inserts only into known sessions.
/// It doesn't scope by user; tests that need that use the SQLite repository.
/// </summary>
public sealed class InMemoryCanvasRepository : ICanvasRepository
{
    private readonly object _gate = new();
    private readonly List<Canvas> _canvases = [];
    private readonly List<CanvasRevision> _revisions = [];
    private readonly HashSet<string> _sessionIds = new(StringComparer.Ordinal);

    /// <summary>Sessions that canvases can be inserted into.</summary>
    public void AddSession(string sessionId)
    {
        lock (_gate)
            _sessionIds.Add(sessionId);
    }

    /// <summary>
    /// Runs once, just before the next <see cref="TryUpdateAsync"/> checks the version, so a test can
    /// slip in a competing write.
    /// </summary>
    public Func<Task>? BeforeNextUpdate { get; set; }

    public IReadOnlyList<CanvasRevision> Revisions
    {
        get
        {
            lock (_gate)
                return [.. _revisions];
        }
    }

    public Task<IReadOnlyList<Canvas>> ListBySessionIdAsync(string sessionId, bool includeClosed = false)
    {
        lock (_gate)
        {
            IReadOnlyList<Canvas> result = [.. _canvases
                .Where(c => c.SessionId == sessionId && (includeClosed || c.ClosedAt is null))
                .OrderBy(c => c.CreatedAt, StringComparer.Ordinal)
                .ThenBy(c => c.Id, StringComparer.Ordinal)
                .Select(Copy)];
            return Task.FromResult(result);
        }
    }

    public Task<Canvas?> GetByIdAsync(string sessionId, string canvasId)
    {
        lock (_gate)
            return Task.FromResult(Find(sessionId, canvasId) is { } canvas ? Copy(canvas) : null);
    }

    public Task<Canvas?> GetByTitleAsync(string sessionId, string title)
    {
        lock (_gate)
        {
            var canvas = _canvases
                .Where(c => c.SessionId == sessionId && c.Title == title)
                .OrderByDescending(c => c.UpdatedAt, StringComparer.Ordinal)
                .ThenByDescending(c => c.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            return Task.FromResult(canvas is null ? null : Copy(canvas));
        }
    }

    public Task<bool> InsertAsync(Canvas canvas, CanvasRevision revision)
    {
        lock (_gate)
        {
            if (!_sessionIds.Contains(canvas.SessionId))
                return Task.FromResult(false);

            _canvases.Add(Copy(canvas));
            _revisions.Add(Copy(revision));
            return Task.FromResult(true);
        }
    }

    public async Task<bool> TryUpdateAsync(Canvas canvas, int expectedVersion, CanvasRevision revision)
    {
        if (BeforeNextUpdate is { } before)
        {
            BeforeNextUpdate = null;
            await before();
        }

        lock (_gate)
        {
            var stored = Find(canvas.SessionId, canvas.Id);
            if (stored is null || stored.Version != expectedVersion)
                return false;

            stored.StateJson = canvas.StateJson;
            stored.Version = canvas.Version;
            stored.AgentSeenVersion = Math.Max(stored.AgentSeenVersion, canvas.AgentSeenVersion);
            stored.UpdatedAt = canvas.UpdatedAt;
            _revisions.Add(Copy(revision));
            return true;
        }
    }

    public Task MarkAgentSeenAsync(string sessionId, string canvasId, int version)
    {
        lock (_gate)
        {
            if (Find(sessionId, canvasId) is { } stored)
                stored.AgentSeenVersion = Math.Max(stored.AgentSeenVersion, Math.Min(version, stored.Version));
            return Task.CompletedTask;
        }
    }

    public Task<bool> SetClosedAtAsync(string sessionId, string canvasId, string? closedAt)
    {
        lock (_gate)
        {
            var stored = Find(sessionId, canvasId);
            if (stored is null)
                return Task.FromResult(false);

            stored.ClosedAt = closedAt;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<CanvasRevision>> ListRevisionsAsync(string canvasId, int afterVersion)
    {
        lock (_gate)
        {
            IReadOnlyList<CanvasRevision> result = [.. _revisions
                .Where(r => r.CanvasId == canvasId && r.Version > afterVersion)
                .OrderBy(r => r.Version)
                .Select(Copy)];
            return Task.FromResult(result);
        }
    }

    private Canvas? Find(string sessionId, string canvasId)
        => _canvases.Find(c => c.SessionId == sessionId && c.Id == canvasId);

    private static Canvas Copy(Canvas c) => new()
    {
        Id = c.Id,
        SessionId = c.SessionId,
        UserId = c.UserId,
        Kind = c.Kind,
        Title = c.Title,
        StateJson = c.StateJson,
        Version = c.Version,
        AgentSeenVersion = c.AgentSeenVersion,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt,
        ClosedAt = c.ClosedAt,
    };

    private static CanvasRevision Copy(CanvasRevision r) => new()
    {
        CanvasId = r.CanvasId,
        Version = r.Version,
        Actor = r.Actor,
        OpsJson = r.OpsJson,
        CreatedAt = r.CreatedAt,
    };
}
