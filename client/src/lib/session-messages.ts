/**
 * Messages between sessions: one session's agent tells another something with the `fleet_message` tool. Fleet
 * delivers it as a prompt wrapped in a tag naming the sender, which it resolved from the calling process:
 *
 *   <fleet-session-message from="ses_…" title="Fix authentication flow">
 *   …text…
 *   </fleet-session-message>
 *
 * The tag is in the text itself, so it survives a reload from the harness's store.
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
