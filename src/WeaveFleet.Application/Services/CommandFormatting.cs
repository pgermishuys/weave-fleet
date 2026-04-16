using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Shared utility for formatting slash command prompts consistently across harness implementations.
/// </summary>
public static class CommandFormatting
{
    /// <summary>
    /// Formats a slash command prompt from <paramref name="options"/>, sanitizing arguments
    /// by collapsing newlines to spaces to prevent prompt injection.
    /// </summary>
    public static string FormatCommandPrompt(CommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sanitizedArgs = options.Arguments?.ReplaceLineEndings(" ");
        return string.IsNullOrWhiteSpace(sanitizedArgs)
            ? $"/{options.Command}"
            : $"/{options.Command} {sanitizedArgs}";
    }
}
