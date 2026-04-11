using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>
/// Harness-internal launch artifacts produced by <c>ClaudeCodeHarness.PrepareRuntimeAsync</c>.
/// Claude Code uses built-in <c>claude auth</c> — no user-supplied credentials are needed,
/// so this record carries no additional runtime data.
/// Opaque to the application layer.
/// </summary>
internal sealed record ClaudeCodeLaunchArtifacts : RuntimeLaunchArtifacts;
