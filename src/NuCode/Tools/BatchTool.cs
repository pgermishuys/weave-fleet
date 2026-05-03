using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace NuCode.Tools;

/// <summary>
/// Executes multiple tool calls in parallel. Each call is independent — partial failures
/// do not stop other calls. The "batch" tool itself is disallowed to prevent recursion.
/// </summary>
internal sealed class BatchTool : INuCodeTool
{
    private const int MinCalls = 1;
    private const int MaxCalls = 25;

    private readonly IToolRegistry _toolRegistry;

    public BatchTool(IToolRegistry toolRegistry)
    {
        _toolRegistry = toolRegistry;
    }

    public string Name => "batch";

    public string Description => "Execute multiple tool calls in parallel. Each call runs independently.";

    public AIFunction ToAIFunction() =>
        AIFunctionFactory.Create(ExecuteAsync, new AIFunctionFactoryOptions
        {
            Name = Name,
            Description = Description,
        });

    [Description("Execute multiple tool calls in parallel. Each call runs independently — partial failures do not stop others.")]
    internal async Task<string> ExecuteAsync(
        [Description("Array of tool calls, each with 'tool' (tool name) and 'parameters' (object). Min 1, max 25.")] JsonElement toolCalls,
        CancellationToken cancellationToken)
    {
        if (toolCalls.ValueKind != JsonValueKind.Array)
        {
            return "Error: tool_calls must be an array.";
        }

        var calls = new List<(string Tool, JsonElement Parameters)>();

        foreach (var element in toolCalls.EnumerateArray())
        {
            if (!element.TryGetProperty("tool", out var toolProp) || toolProp.ValueKind != JsonValueKind.String)
            {
                return "Error: Each tool call must have a 'tool' property (string).";
            }

            var toolName = toolProp.GetString()!;

            if (string.Equals(toolName, "batch", StringComparison.OrdinalIgnoreCase))
            {
                return "Error: Recursive batch calls are not allowed.";
            }

            var parameters = element.TryGetProperty("parameters", out var paramsProp)
                ? paramsProp
                : default;

            calls.Add((toolName, parameters));
        }

        if (calls.Count < MinCalls)
        {
            return "Error: tool_calls must contain at least 1 call.";
        }

        if (calls.Count > MaxCalls)
        {
            return $"Error: tool_calls must contain at most {MaxCalls} calls. Got {calls.Count}.";
        }

        // Execute all calls in parallel
        var tasks = calls.Select((call, index) =>
            ExecuteSingleCallAsync(call.Tool, call.Parameters, index, cancellationToken));

        var results = await Task.WhenAll(tasks);

        // Build summary
        var successes = results.Count(r => r.Success);
        var failures = results.Length - successes;

        var sb = new StringBuilder();
        sb.AppendLine($"Batch complete: {successes} succeeded, {failures} failed out of {results.Length} calls.");
        sb.AppendLine();

        foreach (var result in results)
        {
            sb.AppendLine($"--- Call {result.Index} ({result.ToolName}) [{(result.Success ? "OK" : "FAILED")}] ---");
            sb.AppendLine(result.Output);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<BatchCallResult> ExecuteSingleCallAsync(
        string toolName,
        JsonElement parameters,
        int index,
        CancellationToken cancellationToken)
    {
        try
        {
            var tool = _toolRegistry.Get(toolName);
            if (tool is null)
            {
                return new BatchCallResult(index, toolName, false, $"Error: Unknown tool '{toolName}'.");
            }

            var fn = tool.ToAIFunction();
            var args = ConvertParameters(parameters);
            var result = await fn.InvokeAsync(new AIFunctionArguments(args), cancellationToken);
            return new BatchCallResult(index, toolName, true, result?.ToString() ?? "");
        }
        catch (Exception ex)
        {
            return new BatchCallResult(index, toolName, false, $"Error: {ex.Message}");
        }
    }

    private static Dictionary<string, object?> ConvertParameters(JsonElement parameters)
    {
        var dict = new Dictionary<string, object?>();

        if (parameters.ValueKind != JsonValueKind.Object)
        {
            return dict;
        }

        foreach (var prop in parameters.EnumerateObject())
        {
            dict[prop.Name] = ConvertJsonValue(prop.Value);
        }

        return dict;
    }

    private static object? ConvertJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element, // Arrays and objects pass through as JsonElement
        };
    }

    private sealed record BatchCallResult(int Index, string ToolName, bool Success, string Output);
}
