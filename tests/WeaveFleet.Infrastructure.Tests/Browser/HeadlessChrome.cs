namespace WeaveFleet.Infrastructure.Tests.Browser;

/// <summary>
/// Tests that start a headless Chrome run one class at a time: one test counts the browsers running, and two
/// classes' browsers side by side would also double the memory on a small machine.
/// </summary>
[CollectionDefinition(Collection)]
public sealed class HeadlessChrome
{
    public const string Collection = "Headless Chrome";
}
