using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace NuCode.Tools;

/// <summary>
/// Writes content to a file on the local filesystem, creating parent directories as needed.
/// </summary>
internal sealed class WriteTool : INuCodeTool
{
    public string Name => "write";
    public string Description => "Writes a file to the local filesystem.";

    public AIFunction ToAIFunction() =>
        AIFunctionFactory.Create(ExecuteAsync, new AIFunctionFactoryOptions
        {
            Name = Name,
            Description = Description,
        });

    [Description("Writes a file to the local filesystem.")]
    private static async Task<string> ExecuteAsync(
        [Description("The absolute path to the file to write (must be absolute, not relative)")] string filePath,
        [Description("The content to write to the file")] string content,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "Error: filePath is required.";
        }

        var fullPath = Path.GetFullPath(filePath);

        var isNewFile = !File.Exists(fullPath);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content, cancellationToken);
        }
        catch (IOException ex)
        {
            return $"Error writing file: {ex.Message}";
        }

        return isNewFile
            ? "Wrote file successfully (new file created)."
            : "Wrote file successfully.";
    }
}
