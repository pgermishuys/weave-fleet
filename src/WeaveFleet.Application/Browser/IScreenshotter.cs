namespace WeaveFleet.Application.Browser;

/// <summary>The page to shoot and the window to shoot it in. The url is always a page on this machine.</summary>
public sealed record ScreenshotRequest(string Url, int Width, int Height);

/// <summary>
/// A captured page: PNG bytes at the size they were taken. <see cref="Note"/> is set when the picture comes with
/// a caveat the agent should read before trusting it.
/// </summary>
public sealed record Screenshot(byte[] Png, int Width, int Height, string? Note = null);

/// <summary>Either the image, or why there isn't one, in words the agent can act on.</summary>
public sealed record ScreenshotOutcome(Screenshot? Image, string? Problem)
{
    public static ScreenshotOutcome Ok(byte[] png, int width, int height, string? note = null)
        => new(new Screenshot(png, width, height, note), null);

    public static ScreenshotOutcome Fail(string problem) => new(null, problem);
}

/// <summary>
/// The sizes the agent can ask for. Two is enough to check a layout, and each one costs tokens: a shot is
/// about <c>width × height / 750</c> tokens of the model's context.
/// </summary>
public static class ScreenshotViewports
{
    public const string Desktop = "desktop";
    public const string Phone = "phone";

    public static bool TryResolve(string? viewport, out int width, out int height)
    {
        switch (viewport?.Trim().ToLowerInvariant())
        {
            case "" or null or Desktop:
                (width, height) = (1280, 800);
                return true;
            case Phone:
                (width, height) = (390, 844);
                return true;
            default:
                (width, height) = (0, 0);
                return false;
        }
    }

    public const string Requirement = "\"viewport\" must be \"desktop\" (1280×800) or \"phone\" (390×844).";
}

/// <summary>
/// Takes a picture of a page running on this machine, so an agent can look at the UI it just changed instead
/// of guessing. Implementations drive a browser that is already installed; when there isn't one,
/// <see cref="ScreenshotOutcome.Problem"/> says so rather than throwing.
/// </summary>
public interface IScreenshotter
{
    Task<ScreenshotOutcome> CaptureAsync(ScreenshotRequest request, CancellationToken ct = default);
}
