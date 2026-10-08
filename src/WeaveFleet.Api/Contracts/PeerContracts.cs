namespace WeaveFleet.Api.Contracts;

/// <summary>
/// A message from a session on another machine, which that machine's Fleet sends with its token for this one. The
/// sender is what that Fleet resolved from its own agent's call; see docs/machines.md.
/// </summary>
/// <param name="FromMachineId">The sending machine's id (<c>GET /api/machine</c> there).</param>
/// <param name="FromMachineName">What to call the sending machine.</param>
/// <param name="FromSessionId">The sending session's id on its machine.</param>
/// <param name="FromTitle">The sending session's title.</param>
/// <param name="Text">The message.</param>
public sealed record PeerSessionMessageRequest(string? FromMachineId, string? FromMachineName, string? FromSessionId, string? FromTitle, string? Text);

/// <summary>A delivered peer message.</summary>
/// <param name="SessionId">The session it went to.</param>
/// <param name="Title">That session's title.</param>
/// <param name="MessageId">The id the session's harness was given for it, when it gives one. Its replies name it as their parent.</param>
public sealed record PeerSessionMessageResponse(string SessionId, string Title, string? MessageId);

/// <summary>One page of a session's conversation, as <c>fleet_session_read</c> shows it to an agent.</summary>
public sealed record PeerSessionPageResponse(string Title, string Text);
