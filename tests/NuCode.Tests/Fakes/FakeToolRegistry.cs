using NuCode.Agents;
using NuCode.Tools;

namespace NuCode.Fakes;

internal sealed class FakeToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, INuCodeTool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<INuCodeTool>? _profileTools;

    public void Register(INuCodeTool tool) => _tools[tool.Name] = tool;

    public INuCodeTool? Get(string id) => _tools.GetValueOrDefault(id);

    public IReadOnlyList<INuCodeTool> GetAll() => _tools.Values.ToList();

    public IReadOnlyList<INuCodeTool> GetForProfile(AgentProfile profile) =>
        _profileTools ?? GetAll();

    /// <summary>
    /// Sets the tools returned by <see cref="GetForProfile"/>. If not set, returns all registered tools.
    /// </summary>
    public void SetProfileTools(IReadOnlyList<INuCodeTool> tools) => _profileTools = tools;
}
