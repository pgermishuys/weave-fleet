using NuCode.ConformanceTests.NuCode;

namespace NuCode.ConformanceTests.NuCode.Gaps;

/// <summary>
/// Documents that the domain <see cref="MessagePart"/> hierarchy does not include
/// agent delegation, subtask, or patch/diff part types. These part types exist in
/// OpenCode's internal model (<c>OpenCodeAgentPart</c>, <c>OpenCodeSubtaskPart</c>,
/// <c>OpenCodePatchPart</c>) but are silently dropped by <c>OpenCodeMapper.MapPart</c>
/// (returns <c>null</c> for unrecognised types). This is a domain model gap that
/// affects both harnesses, not a NuCode-specific limitation.
///
/// These tests are expected to FAIL until the domain model is extended with new
/// <see cref="MessagePart"/> subtypes and both mappers are updated.
/// </summary>
[Trait("Gap", "advanced-parts")]
public sealed class AdvancedPartGapTests
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
