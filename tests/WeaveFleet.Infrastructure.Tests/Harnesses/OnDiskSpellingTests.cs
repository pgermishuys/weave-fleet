using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Tests.Harnesses;

public sealed class OnDiskSpellingTests
{
    // A Windows disk: C:\source\products-private-worktrees\pg-pull-latest and C:\Users\Jo Smith.
    private static readonly Dictionary<string, string[]> Disk = new(StringComparer.Ordinal)
    {
        [@"C:\"] = ["source", "Users"],
        [@"C:\source\"] = ["products-private-worktrees"],
        [@"C:\source\products-private-worktrees\"] = ["pg-pull-latest"],
        [@"C:\Users\"] = ["Jo Smith"],
        ["C:/"] = ["source"],
        ["C:/source/"] = ["products-private-worktrees"],
    };

    private static string? NameOnDisk(string parent, string name)
        => Disk.TryGetValue(parent, out var children)
            ? children.FirstOrDefault(child => string.Equals(child, name, StringComparison.OrdinalIgnoreCase))
            : null;

    [Theory]
    [InlineData(@"c:\source\products-private-worktrees\pg-pull-latest", @"C:\source\products-private-worktrees\pg-pull-latest")]
    [InlineData(@"C:\SOURCE\Products-Private-Worktrees\PG-Pull-Latest", @"C:\source\products-private-worktrees\pg-pull-latest")]
    [InlineData(@"c:\users\jo smith", @"C:\Users\Jo Smith")]
    [InlineData(@"C:\source\products-private-worktrees\pg-pull-latest", @"C:\source\products-private-worktrees\pg-pull-latest")]
    public void Spells_each_folder_as_it_is_on_disk_with_a_capital_drive_letter(string given, string expected)
    {
        HarnessHelpers.OnDiskSpelling(given, NameOnDisk).ShouldBe(expected);
    }

    [Theory]
    [InlineData(@"c:\source\", @"C:\source\")]
    [InlineData("c:/source/products-private-worktrees", "C:/source/products-private-worktrees")]
    [InlineData(@"c:\source\.\products-private-worktrees", @"C:\source\.\products-private-worktrees")]
    public void Keeps_the_separators_it_was_given(string given, string expected)
    {
        HarnessHelpers.OnDiskSpelling(given, NameOnDisk).ShouldBe(expected);
    }

    [Fact]
    public void Leaves_the_rest_as_given_from_the_first_folder_that_is_not_there()
    {
        HarnessHelpers.OnDiskSpelling(@"c:\SOURCE\Gone\Products-Private-Worktrees", NameOnDisk)
            .ShouldBe(@"C:\source\Gone\Products-Private-Worktrees");
    }

    [Theory]
    [InlineData("/home/jo/source")]
    [InlineData(@"\\server\share\source")]
    [InlineData("c:")]
    public void Leaves_a_path_that_is_not_on_a_drive_as_given(string given)
    {
        HarnessHelpers.OnDiskSpelling(given, (_, _) => throw new InvalidOperationException("looked up")).ShouldBe(given);
    }

    [Fact]
    public void Reads_a_folder_name_from_disk_ignoring_case()
    {
        var parent = Directory.CreateTempSubdirectory("fleet-spelling-");
        try
        {
            parent.CreateSubdirectory("Source");

            HarnessHelpers.NameOnDisk(parent.FullName, "source").ShouldBe("Source");
            HarnessHelpers.NameOnDisk(parent.FullName, "missing").ShouldBeNull();
            HarnessHelpers.NameOnDisk(parent.FullName, "*").ShouldBeNull();
            HarnessHelpers.NameOnDisk(Path.Combine(parent.FullName, "gone"), "source").ShouldBeNull();
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }
}
