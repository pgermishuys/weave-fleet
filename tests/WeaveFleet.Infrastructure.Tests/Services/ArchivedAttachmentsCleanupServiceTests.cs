using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Sessions;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Services;

/// <summary>Attached files of long-archived sessions go: <see cref="ArchivedAttachmentsCleanupService"/>.</summary>
public sealed class ArchivedAttachmentsCleanupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fleet-archived-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemorySessionRepository _sessions = new();
    private readonly ArchivedAttachmentsCleanupService _sut;

    public ArchivedAttachmentsCleanupServiceTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISessionRepository>(_sessions);
        _sut = new ArchivedAttachmentsCleanupService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _time, NullLogger<ArchivedAttachmentsCleanupService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Seed(string id, string retention, DateTimeOffset? archivedAt)
    {
        var directory = Path.Combine(_root, id);
        Directory.CreateDirectory(Path.Combine(directory, ".weave", "attachments", id[..8]));
        File.WriteAllText(Path.Combine(directory, ".weave", "attachments", id[..8], "a.log"), "x");
        _sessions.Seed(new Session
        {
            Id = id, InstanceId = "inst-" + id, Directory = directory, RetentionStatus = retention,
            ArchivedAt = archivedAt?.UtcDateTime.ToString("O"), UserId = "user-1",
        });
        return Path.Combine(directory, ".weave");
    }

    [Fact]
    public async Task deletes_the_files_of_sessions_archived_over_thirty_days_ago_only()
    {
        var old = Seed("sess-old-1", "archived", _time.GetUtcNow().AddDays(-31));
        var recent = Seed("sess-new-1", "archived", _time.GetUtcNow().AddDays(-29));
        var active = Seed("sess-act-1", "active", null);

        (await _sut.SweepAsync()).ShouldBe(1);

        Directory.Exists(old).ShouldBeFalse();
        Directory.Exists(recent).ShouldBeTrue();
        Directory.Exists(active).ShouldBeTrue();
    }

    [Fact]
    public async Task a_later_sweep_takes_a_session_that_has_since_passed_thirty_days()
    {
        var weave = Seed("sess-old-1", "archived", _time.GetUtcNow().AddDays(-29));
        (await _sut.SweepAsync()).ShouldBe(0);

        _time.Advance(TimeSpan.FromDays(2));

        (await _sut.SweepAsync()).ShouldBe(1);
        Directory.Exists(weave).ShouldBeFalse();
        (await _sut.SweepAsync()).ShouldBe(0);
    }
}
