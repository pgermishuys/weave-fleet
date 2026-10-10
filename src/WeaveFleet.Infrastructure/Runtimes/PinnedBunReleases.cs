using WeaveFleet.Application.Runtimes;

namespace WeaveFleet.Infrastructure.Runtimes;

/// <summary>The release built into Fleet, until Fleet reads its manifest.</summary>
internal sealed class PinnedBunReleases : IBunReleases
{
    /// <inheritdoc />
    public BunRelease Current => BunRelease.Pinned;

    /// <inheritdoc />
    public event EventHandler<BunReleaseChangedEventArgs>? Changed
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    Task IBunReleases.RefreshAsync(CancellationToken ct) => Task.CompletedTask;
}
