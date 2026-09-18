/**
 * Messages between sessions: one session's agent tells another something with the `fleet_message` tool. Fleet
 * delivers it as a prompt wrapped in a tag naming the sender, which it resolved from the calling process:
 *
 *   <fleet-session-message from="ses_…" title="Fix authentication flow">
 *   …text…
 *   </fleet-session-message>
 *
 * When the sender asked to hear back, Fleet tells it once the other session's turn ends, with a different tag so
 * the update can't pass for something that session's agent wrote:
 *
 *   <fleet-session-update session="ses_…" title="Update documentation" outcome="finished">
 *   …its last reply, or the failure…
 *   </fleet-session-update>
 *
 * The tags are in the text itself, so they survive a reload from the harness's store.
 */

/** The user preference behind the switch in Settings → Features. */
export const SESSION_MESSAGES_PREFERENCE_KEY = "SessionMessages";

export interface PeerSender {
  sessionId: string;
  title: string;
}

const PEER_MESSAGE = /^<fleet-session-message from="([^"]*)" title="([^"]*)">\n?([\s\S]*?)\n?<\/fleet-session-message>\s*$/;

// Fleet HTML-encodes the id and title in the tag.
const ENTITIES: Record<string, string> = { "&amp;": "&", "&quot;": "\"", "&#39;": "'", "&lt;": "<", "&gt;": ">" };

function decode(value: string): string {
  return value.replace(/&(amp|quot|#39|lt|gt);/g, (entity) => ENTITIES[entity]);
}

/** The sender and the text of a message another session sent, or null for anything else. */
export function parsePeerMessage(body: string): { peer: PeerSender; text: string } | null {
  const match = PEER_MESSAGE.exec(body);
  if (!match) return null;
  return { peer: { sessionId: decode(match[1]), title: decode(match[2]) }, text: match[3] };
}

export type PeerOutcome = "finished" | "failed";

const PEER_UPDATE = /^<fleet-session-update session="([^"]*)" title="([^"]*)" outcome="(finished|failed)">\n?([\s\S]*?)\n?<\/fleet-session-update>\s*$/;

/** The session, how its turn ended and what it said, for an update Fleet sent; null for anything else. */
export function parsePeerUpdate(body: string): { peer: PeerSender; outcome: PeerOutcome; text: string } | null {
  const match = PEER_UPDATE.exec(body);
  if (!match) return null;
  return {
    peer: { sessionId: decode(match[1]), title: decode(match[2]) },
    outcome: match[3] as PeerOutcome,
    text: match[4],
  };
}
