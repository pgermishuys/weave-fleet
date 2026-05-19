using NuCode.ConformanceTests.NuCode;

namespace NuCode.ConformanceTests.NuCode.Gaps;

/// <summary>
/// Validates that the domain <see cref="MessagePart"/> hierarchy includes
/// <c>AgentPart</c>, <c>SubtaskPart</c>, and <c>PatchPart</c> types for
/// representing sub-agent delegation, child session activity, and file edit diffs.
/// The OpenCode mapper now maps these from <c>OpenCodeAgentPart</c>,
/// <c>OpenCodeSubtaskPart</c>, and <c>OpenCodePatchPart</c> respectively.
/// </summary>
[Trait("Gap", "advanced-parts")]
public sealed class AdvancedPartTests
{
    [Fact]
    public void MessagePartHierarchy_IncludesAgentPartType()
    {
        // The domain MessagePart hierarchy should include an AgentPart type
        // for representing sub-agent delegation in messages.
        // Currently missing from WeaveFleet.Domain.Harnesses.HarnessTypes.
        var partTypes = typeof(MessagePart).Assembly
            .GetTypes()
            .Where(t => t.IsSubclassOf(typeof(MessagePart)) && !t.IsAbstract)
            .Select(t => t.Name)
            .ToList();

        partTypes.ShouldContain("AgentPart",
            "MessagePart hierarchy does not include AgentPart. " +
            "Both NuCode and OpenCode mappers drop agent delegation parts.");
    }

    [Fact]
    public void MessagePartHierarchy_IncludesSubtaskPartType()
    {
        // The domain MessagePart hierarchy should include a SubtaskPart type
        // for representing child session activity in messages.
        var partTypes = typeof(MessagePart).Assembly
            .GetTypes()
            .Where(t => t.IsSubclassOf(typeof(MessagePart)) && !t.IsAbstract)
            .Select(t => t.Name)
            .ToList();

        partTypes.ShouldContain("SubtaskPart",
            "MessagePart hierarchy does not include SubtaskPart. " +
            "Both NuCode and OpenCode mappers drop subtask parts.");
    }

    [Fact]
    public void MessagePartHierarchy_IncludesPatchPartType()
    {
        // The domain MessagePart hierarchy should include a PatchPart type
        // for representing file edit diffs in messages.
        var partTypes = typeof(MessagePart).Assembly
            .GetTypes()
            .Where(t => t.IsSubclassOf(typeof(MessagePart)) && !t.IsAbstract)
            .Select(t => t.Name)
            .ToList();

        partTypes.ShouldContain("PatchPart",
            "MessagePart hierarchy does not include PatchPart. " +
            "Both NuCode and OpenCode mappers drop patch/diff parts.");
    }
}
