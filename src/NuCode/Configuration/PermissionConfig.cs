using System.Text.Json;
using System.Text.Json.Serialization;

namespace NuCode.Configuration;

/// <summary>
/// Permission configuration. Each key is a permission type (read, edit, bash, glob, grep, task, etc.).
/// Values are either:
/// - A simple action string: "allow", "ask", or "deny" (applies to all patterns)
/// - A dictionary mapping glob patterns to actions
/// </summary>
public sealed class PermissionConfig
{
    /// <summary>
    /// Permission rules keyed by permission type.
    /// Value is either a string ("allow"/"ask"/"deny") or a Dictionary&lt;string, string&gt; of pattern→action.
    /// </summary>
    [JsonPropertyName("rules")]
    public Dictionary<string, PermissionRuleConfig>? Rules { get; init; }
}

/// <summary>
/// A permission rule that is either a simple action or a pattern map.
/// Requires a custom JsonConverter since it's a union type.
/// </summary>
[JsonConverter(typeof(PermissionRuleConfigJsonConverter))]
public sealed class PermissionRuleConfig
{
    /// <summary>
    /// Simple action (when value is just "allow"/"ask"/"deny").
    /// </summary>
    public string? Action { get; init; }

    /// <summary>
    /// Pattern-based rules (when value is { "pattern": "action" }).
    /// </summary>
    public Dictionary<string, string>? PatternRules { get; init; }

    /// <summary>
    /// Returns <c>true</c> when this rule is a simple action string rather than a pattern map.
    /// </summary>
    public bool IsSimple => Action is not null;
}

/// <summary>
/// Custom JSON converter for <see cref="PermissionRuleConfig"/> that handles
/// the union type: either a string ("allow"/"ask"/"deny") or an object (pattern→action map).
/// </summary>
public sealed class PermissionRuleConfigJsonConverter : JsonConverter<PermissionRuleConfig>
{
    public override PermissionRuleConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var action = reader.GetString();
            return new PermissionRuleConfig { Action = action };
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var patternRules = new Dictionary<string, string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var pattern = reader.GetString()!;
                reader.Read();
                var action = reader.GetString()!;
                patternRules[pattern] = action;
            }
            return new PermissionRuleConfig { PatternRules = patternRules };
        }

        throw new JsonException(
            $"Expected string or object for {nameof(PermissionRuleConfig)}, got {reader.TokenType}.");
    }

    public override void Write(Utf8JsonWriter writer, PermissionRuleConfig value, JsonSerializerOptions options)
    {
        if (value.IsSimple)
        {
            writer.WriteStringValue(value.Action);
        }
        else if (value.PatternRules is not null)
        {
            writer.WriteStartObject();
            foreach (var (pattern, action) in value.PatternRules)
            {
                writer.WriteString(pattern, action);
            }
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
