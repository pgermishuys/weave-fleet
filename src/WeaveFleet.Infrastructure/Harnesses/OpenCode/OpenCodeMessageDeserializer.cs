using System.Text.Json;
using System.Text.Json.Nodes;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// Deserializes OpenCode message info payloads while tolerating alternate property casing.
/// </summary>
internal static class OpenCodeMessageDeserializer
{
    internal static OpenCodeAssistantMessage? DeserializeAssistantMessage(JsonElement infoEl)
        => Deserialize<OpenCodeAssistantMessage>(infoEl, static node =>
        {
            NormalizeCommonMessageInfoAliases(node);
            ApplyAlias(node, "parentId", "parentID", "parent_id");
            ApplyAlias(node, "providerId", "providerID", "provider_id");
            ApplyAlias(node, "modelId", "modelID", "model_id");
        });

    internal static OpenCodeUserMessage? DeserializeUserMessage(JsonElement infoEl)
        => Deserialize<OpenCodeUserMessage>(infoEl, static node =>
        {
            NormalizeCommonMessageInfoAliases(node);

            if (node["model"] is JsonObject modelNode)
            {
                ApplyAlias(modelNode, "providerId", "providerID", "provider_id");
                ApplyAlias(modelNode, "modelId", "modelID", "model_id");
            }
        });

    private static T? Deserialize<T>(JsonElement element, Action<JsonObject> normalize)
        where T : class
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var node = JsonNode.Parse(element.GetRawText()) as JsonObject;
        if (node is null)
            return null;

        normalize(node);
        return node.Deserialize<T>(OpenCodeJsonOptions.Default);
    }

    private static void NormalizeCommonMessageInfoAliases(JsonObject node)
    {
        ApplyAlias(node, "sessionId", "sessionID", "session_id");
    }

    private static void ApplyAlias(JsonObject node, string canonicalName, params string[] aliasNames)
    {
        if (node[canonicalName] is not null)
            return;

        foreach (var aliasName in aliasNames)
        {
            if (node[aliasName] is null)
                continue;

            node[canonicalName] = node[aliasName]?.DeepClone();
            return;
        }
    }
}
