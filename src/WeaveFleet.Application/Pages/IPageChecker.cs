namespace WeaveFleet.Application.Pages;

/// <summary>
/// What a page check found, one line per problem in words the agent can act on; none means the page is fine.
/// <see cref="Problem"/> is set instead when Fleet couldn't check the page at all.
/// </summary>
public sealed record PageCheckOutcome(IReadOnlyList<string> Findings, string? Problem)
{
    public static PageCheckOutcome Found(IReadOnlyList<string> findings) => new(findings, null);

    public static PageCheckOutcome Fail(string problem) => new([], problem);
}

/// <summary>
/// Loads a page Fleet serves and reports, as text, what an agent would otherwise need a screenshot to notice:
/// script errors, files that didn't load, a page with nothing on it, and anything wider than a desktop or a
/// phone window. A line of text costs the model a few dozen tokens where a picture costs a thousand or more.
/// </summary>
public interface IPageChecker
{
    /// <summary>The window widths a page is checked at: the screenshot tool's desktop and phone.</summary>
    public static readonly int[] Widths = [1280, 390];

    Task<PageCheckOutcome> CheckAsync(string url, CancellationToken ct = default);
}
