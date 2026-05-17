using System.Text;
using System.Text.Json;

namespace NuCode.Configuration;

/// <summary>
/// Parses JSONC (JSON with Comments) by stripping single-line (//) and multi-line (/* */) comments
/// before delegating to <see cref="System.Text.Json.JsonSerializer"/>.
/// </summary>
internal static class JsoncParser
{
    /// <summary>
    /// Deserializes a JSONC string into the specified type, stripping comments first.
    /// </summary>
    internal static T? Deserialize<T>(string jsonc, JsonSerializerOptions? options)
    {
        var json = StripComments(jsonc);
        return JsonSerializer.Deserialize<T>(json, options);
    }

    /// <summary>
    /// Strips single-line (//) and multi-line (/* */) comments from JSONC text.
    /// Preserves string literals and handles escaped quotes correctly.
    /// </summary>
    internal static string StripComments(string jsonc)
    {
        var sb = new StringBuilder(jsonc.Length);
        var i = 0;
        var length = jsonc.Length;

        while (i < length)
        {
            var c = jsonc[i];

            // String literal — copy verbatim including escape sequences
            if (c == '"')
            {
                sb.Append(c);
                i++;
                while (i < length)
                {
                    var sc = jsonc[i];
                    sb.Append(sc);
                    if (sc == '\\' && i + 1 < length)
                    {
                        // Escaped character — copy the next char unconditionally
                        i++;
                        sb.Append(jsonc[i]);
                        i++;
                        continue;
                    }
                    if (sc == '"')
                    {
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            // Potential comment start
            if (c == '/' && i + 1 < length)
            {
                var next = jsonc[i + 1];

                // Single-line comment: skip until end of line
                if (next == '/')
                {
                    i += 2;
                    while (i < length && jsonc[i] != '\n')
                    {
                        i++;
                    }
                    continue;
                }

                // Multi-line comment: skip until closing */
                if (next == '*')
                {
                    i += 2;
                    while (i + 1 < length)
                    {
                        if (jsonc[i] == '*' && jsonc[i + 1] == '/')
                        {
                            i += 2;
                            break;
                        }
                        i++;
                    }
                    // Handle edge case: unclosed multi-line comment at end of input
                    if (i >= length - 1 && !(i >= 2 && jsonc[i - 2] == '*' && jsonc[i - 1] == '/'))
                    {
                        i = length;
                    }
                    continue;
                }
            }

            // Regular character — copy as-is
            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
