using Microsoft.Extensions.AI;

namespace NuCode.Fakes;

internal sealed class FakeNuCodeTool : INuCodeTool
{
    private readonly AIFunction _function;

    public FakeNuCodeTool(string name, string description, string returnValue)
    {
        Name = name;
        Description = description;
        _function = AIFunctionFactory.Create(
            () => returnValue,
            new AIFunctionFactoryOptions { Name = name, Description = description });
    }

    public FakeNuCodeTool(string name, string description, AIFunction function)
    {
        Name = name;
        Description = description;
        _function = function;
    }

    public string Name { get; }
    public string Description { get; }
    public AIFunction ToAIFunction() => _function;
}
