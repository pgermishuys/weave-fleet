using System.Text.RegularExpressions;
using WeaveFleet.Domain.Common;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// Skills the user named with <c>$</c> in a message (<c>$fleet-debug</c>). The text goes to the harness as typed; the
/// composer sends the names beside it. Each harness decides how they reach the agent: Fleet only checks the names
/// against the text, and writes the note for a harness that has no way of its own.
/// </summary>
public static partial class SkillMentions
{
    /// <summary>The most skills one message can name.</summary>
    public const int MaxSkills = 10;

    /// <summary>A skill's name: letters and digits first, then those and <c>_ . : -</c> (Claude Code plugin skills are <c>plugin:skill</c>).</summary>
    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N}_.:-]{0,63}$")]
    public static partial Regex NamePattern();

    /// <summary>
    /// The names whose <c>$name</c> token is in the text, once each, in the order they first appear. A name with no token
    /// is dropped (the user deleted it), as a reference's token is; an invalid name is an error.
    /// </summary>
    public static Result<IReadOnlyList<string>> Validate(string text, IReadOnlyList<string>? skills)
    {
        if (skills is null or { Count: 0 })
            return Array.Empty<string>();

        var found = new List<(int Index, string Name)>();
        foreach (var name in skills.Distinct(StringComparer.Ordinal))
        {
            if (!NamePattern().IsMatch(name))
                return FleetError.ValidationError("Skills", $"\"{name}\" isn't a skill name.");

            var index = IndexOfToken(text, name);
            if (index >= 0)
                found.Add((index, name));
        }

        if (found.Count > MaxSkills)
            return FleetError.ValidationError("Skills", $"A message can name at most {MaxSkills} skills.");

        return found.OrderBy(f => f.Index).Select(f => f.Name).ToList();
    }

    /// <summary>What the model is told about the skills, for a harness that doesn't send them itself.</summary>
    public static string Note(IReadOnlyList<string> names) => names.Count == 1
        ? $"The user named this skill: {names[0]}. Load it with your skill tool before the step it's for."
        : $"The user named these skills: {string.Join(", ", names)}. Load each with your skill tool before the step it's for.";

    /// <summary>
    /// Where <c>$name</c> stands in the text, or -1. The <c>$</c> starts the text or follows whitespace or one of
    /// <c>( [ " '</c>; the name ends the text or is followed by something a name can't continue with (a <c>.</c> only
    /// continues it when a letter or digit follows, so a sentence-ending one doesn't).
    /// </summary>
    private static int IndexOfToken(string text, string name)
    {
        var token = "$" + name;
        for (var at = text.IndexOf(token, StringComparison.Ordinal); at >= 0; at = text.IndexOf(token, at + 1, StringComparison.Ordinal))
        {
            if (at > 0 && !char.IsWhiteSpace(text[at - 1]) && "([\"'".IndexOf(text[at - 1]) < 0)
                continue;

            var end = at + token.Length;
            if (end >= text.Length)
                return at;

            var next = text[end];
            var continues = char.IsLetterOrDigit(next) || next is '_' or '-' or ':'
                || (next == '.' && end + 1 < text.Length && char.IsLetterOrDigit(text[end + 1]));
            if (!continues)
                return at;
        }

        return -1;
    }
}
