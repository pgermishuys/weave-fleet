namespace WeaveFleet.Domain.Common;

/// <summary>Cuts text to a length without splitting an emoji or other character made of two UTF-16 halves.</summary>
public static class TextCut
{
    /// <summary>
    /// The first <paramref name="length"/> characters of <paramref name="text"/>, one fewer when the cut would
    /// leave the first half of a surrogate pair at the end (which every encoder turns into U+FFFD).
    /// </summary>
    public static string Head(string text, int length)
    {
        if (length <= 0)
            return string.Empty;
        if (text.Length <= length)
            return text;
        if (char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
            length--;
        return text[..length];
    }
}
