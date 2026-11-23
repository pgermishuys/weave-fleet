using WeaveFleet.Application.Workspaces;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// On Windows and macOS a folder is the same folder in any letter case, so Fleet must spell it as it is on disk:
/// otherwise <c>c:\src\shop</c> and <c>C:\Src\Shop</c> become two workspace roots. The tests run the real code on
/// Linux's real folders with a lookup that ignores case, the way those file systems answer.
/// </summary>
public sealed class CanonicalizePathCaseTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "fleet-case-" + Guid.NewGuid().ToString("N"));

    public CanonicalizePathCaseTests() => Directory.CreateDirectory(Path.Combine(_base, "Src", "Shop"));

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private static string? CaseInsensitiveNameOnDisk(string parent, string name)
        => Directory.EnumerateDirectories(parent)
            .Select(Path.GetFileName)
            .FirstOrDefault(child => string.Equals(child, name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Spells_each_folder_as_it_is_on_disk()
    {
        var typed = Path.Combine(_base, "src", "SHOP");

        WorkspaceRootService.CanonicalizePath(typed, CaseInsensitiveNameOnDisk)
            .ShouldBe(WorkspaceRootService.CanonicalizePath(Path.Combine(_base, "Src", "Shop")));
        WorkspaceRootService.CanonicalizePath(typed, CaseInsensitiveNameOnDisk)
            .ShouldEndWith(Path.Combine("Src", "Shop"));
    }

    [Fact]
    public void Keeps_the_rest_as_typed_once_a_folder_is_missing()
    {
        var typed = Path.Combine(_base, "src", "Nope", "Deeper");

        WorkspaceRootService.CanonicalizePath(typed, CaseInsensitiveNameOnDisk)
            .ShouldEndWith(Path.Combine("Src", "Nope", "Deeper"));
    }

    [Fact]
    public void A_case_sensitive_file_system_keeps_the_case_it_was_given()
    {
        // The real lookup on this machine: exact on Linux, so a differently-cased path stays as typed.
        if (!OperatingSystem.IsLinux())
            return;

        var typed = Path.Combine(_base, "src", "shop");

        WorkspaceRootService.CanonicalizePath(typed).ShouldEndWith(Path.Combine("src", "shop"));
    }
}
