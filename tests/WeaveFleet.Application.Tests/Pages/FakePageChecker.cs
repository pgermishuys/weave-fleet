using WeaveFleet.Application.Pages;

namespace WeaveFleet.Application.Tests.Pages;

/// <summary>Drives no browser: it records the pages it was asked to check and hands back <see cref="Next"/>.</summary>
internal sealed class FakePageChecker : IPageChecker
{
    public List<string> Urls { get; } = [];

    public PageCheckOutcome Next { get; set; } = PageCheckOutcome.Found([]);

    public Task<PageCheckOutcome> CheckAsync(string url, CancellationToken ct = default)
    {
        Urls.Add(url);
        return Task.FromResult(Next);
    }
}
