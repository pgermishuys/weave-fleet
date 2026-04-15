using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Testing.Fakes;

public sealed class FakeHarness : IHarness
{
    public FakeHarness(string type, string displayName, HarnessCapabilities? capabilities = null)
    {
        Type = type;
        DisplayName = displayName;
        Capabilities = capabilities ?? new HarnessCapabilities();
    }

    public string Type { get; }
    public string DisplayName { get; }
    public HarnessCapabilities Capabilities { get; set; }
}
