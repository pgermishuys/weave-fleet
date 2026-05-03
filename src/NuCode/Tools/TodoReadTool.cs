using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace NuCode.Tools;

/// <summary>
/// Reads the current task list for the session.
/// Returns an empty list until session state backing is implemented.
/// </summary>
internal sealed class TodoReadTool : INuCodeTool
{
    public string Name => "todoread";
    public string Description => "Read the current task list for the session.";

    public AIFunction ToAIFunction() =>
        AIFunctionFactory.Create(ExecuteAsync, new AIFunctionFactoryOptions
        {
            Name = Name,
            Description = Description,
        });

    [Description("Read the current task list for the session.")]
    private static Task<string> ExecuteAsync(
        CancellationToken cancellationToken)
    {
        return Task.FromResult("[]");
    }
}
