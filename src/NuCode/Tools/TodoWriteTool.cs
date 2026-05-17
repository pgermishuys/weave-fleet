using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace NuCode.Tools;

/// <summary>
/// Creates and manages a structured task list for the current session.
/// Validates and echoes back todos as JSON; persistence will be added in a later phase.
/// </summary>
internal sealed class TodoWriteTool : INuCodeTool
{
    private static readonly HashSet<string> s_validStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "pending", "in_progress", "completed", "cancelled",
    };

    private static readonly HashSet<string> s_validPriorities = new(StringComparer.OrdinalIgnoreCase)
    {
        "high", "medium", "low",
    };

    public string Name => "todowrite";
    public string Description => "Create and manage a structured task list for the current session.";

    public AIFunction ToAIFunction() =>
        AIFunctionFactory.Create(ExecuteAsync, new AIFunctionFactoryOptions
        {
            Name = Name,
            Description = Description,
        });

    [Description("Create and manage a structured task list for the current session.")]
    private static Task<string> ExecuteAsync(
        [Description("The updated todo list as a JSON array of objects with content, status, and priority fields")] string todos,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(todos))
        {
            return Task.FromResult("Error: Invalid todos format. Expected a JSON array of objects with content, status, and priority fields.");
        }

        JsonArray array;
        try
        {
            var node = JsonNode.Parse(todos);
            if (node is not JsonArray parsed)
            {
                return Task.FromResult("Error: Invalid todos format. Expected a JSON array of objects with content, status, and priority fields.");
            }
            array = parsed;
        }
        catch (JsonException)
        {
            return Task.FromResult("Error: Invalid todos format. Expected a JSON array of objects with content, status, and priority fields.");
        }

        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject obj)
            {
                return Task.FromResult($"Error: Todo at index {i} must be an object.");
            }

            var content = obj["content"]?.GetValue<string>();
            var status = obj["status"]?.GetValue<string>();
            var priority = obj["priority"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(content))
            {
                return Task.FromResult($"Error: Todo at index {i} is missing required field 'content'.");
            }

            if (string.IsNullOrWhiteSpace(status))
            {
                return Task.FromResult($"Error: Todo at index {i} is missing required field 'status'.");
            }

            if (string.IsNullOrWhiteSpace(priority))
            {
                return Task.FromResult($"Error: Todo at index {i} is missing required field 'priority'.");
            }

            if (!s_validStatuses.Contains(status))
            {
                return Task.FromResult($"Error: Todo at index {i} has invalid status '{status}'. Valid values: pending, in_progress, completed, cancelled.");
            }

            if (!s_validPriorities.Contains(priority))
            {
                return Task.FromResult($"Error: Todo at index {i} has invalid priority '{priority}'. Valid values: high, medium, low.");
            }
        }

        var result = array.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return Task.FromResult(result);
    }
}
