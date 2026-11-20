namespace WeaveFleet.Application.Machines;

/// <summary>
/// "Your Fleet": every machine keeps the list of the machines you've connected, and a screen connected to any of them
/// gets its own key for the others. Always on (it left Labs on 20 November 2026); there is no switch. See
/// .weave/plans/machines-setup-journeys.md.
/// </summary>
public static class YourFleet
{
    /// <summary>The id a screen (a browser or the desktop app's window) gives itself, so its keys can be told apart and replaced.</summary>
    public const int MaxScreenIdLength = 64;

    /// <summary>The prefix of the grant row's device id for a screen, which isn't a paired device.</summary>
    public const string ScreenPrefix = "screen:";

    public static bool IsValidScreenId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= MaxScreenIdLength && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
