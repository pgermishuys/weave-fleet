using WeaveFleet.Application.Browser;

namespace WeaveFleet.Application.Tests.Browser;

/// <summary>Drives no browser: it records what it was asked for and hands back <see cref="NextPng"/>.</summary>
internal sealed class FakeScreenshotter : IScreenshotter
{
    public List<ScreenshotRequest> Requests { get; } = [];

    public byte[] NextPng { get; set; } = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Set to fail the next capture the way a missing browser or a dead page would.</summary>
    public string? NextProblem { get; set; }

    public Task<ScreenshotOutcome> CaptureAsync(ScreenshotRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(NextProblem is { } problem
            ? ScreenshotOutcome.Fail(problem)
            : ScreenshotOutcome.Ok(NextPng, request.Width, request.Height));
    }
}
