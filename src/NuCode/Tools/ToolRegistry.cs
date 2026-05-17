using System.Collections.Concurrent;
using NuCode.Agents;

namespace NuCode.Tools;

/// <summary>
/// Default implementation of <see cref="IToolRegistry"/>.
/// Thread-safe tool registration and lookup.
/// </summary>
internal sealed class ToolRegistry : IToolRegistry
{
    private readonly ConcurrentDictionary<string, INuCodeTool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public void Register(INuCodeTool tool)
    {
        if (!_tools.TryAdd(tool.Name, tool))
        {
            throw new InvalidOperationException($"A tool with name '{tool.Name}' is already registered.");
        }
    }

    public IReadOnlyList<INuCodeTool> GetAll() =>
        _tools.Values.ToList().AsReadOnly();

    public IReadOnlyList<INuCodeTool> GetForProfile(AgentProfile profile)
    {
        var tools = _tools.Values.AsEnumerable();

        if (profile.AllowedTools is not null)
        {
            tools = tools.Where(t => profile.AllowedTools.Contains(t.Name));
        }

        if (profile.DeniedTools is not null)
        {
            tools = tools.Where(t => !profile.DeniedTools.Contains(t.Name));
        }

        return tools.ToList().AsReadOnly();
    }

    public INuCodeTool? Get(string id) =>
        _tools.GetValueOrDefault(id);
}
