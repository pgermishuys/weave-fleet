/**
 * One machine for the phone's inbox: its sessions, and what the waiting ones wait on, kept live by a
 * {@link MachineFeed}. Home is reached with the phone's cookie; any other machine with the phone's own token for it
 * (a device grant). No Vue, so a native wrapper can reuse it.
 */
import type { SessionListItem } from "@/api/client";
import type { PermissionAsk } from "@/composables/use-session-permissions";
import { toPermissionAsk } from "@/composables/use-session-permissions";
import { convertFleetMessageToAccumulated, type FleetMessage } from "@/lib/pagination-utils";
import { FeedError, MachineFeed, type FeedRequest, type FeedSnapshot, type MachineFeedOptions } from "@/lib/machine-feed";
import type { InboxAsk } from "@/lib/phone/inbox";
import { pendingQuestion } from "@/lib/phone/dock-state";

const SESSION_PAGE = 100;

/** What the inbox reads from a machine. */
export interface InboxRead {
  sessions: SessionListItem[];
  asks: Record<string, InboxAsk>;
}

export type InboxSnapshot = FeedSnapshot<InboxRead>;

export type InboxFeedOptions = Omit<MachineFeedOptions<InboxRead>, "initial" | "read">;

export class InboxFeed extends MachineFeed<InboxRead> {
  constructor(options: InboxFeedOptions) {
    super({ ...options, initial: { sessions: [], asks: {} }, read: readInbox });
  }
}

/** Reads the session list and the asks of waiting sessions. */
async function readInbox(request: FeedRequest): Promise<InboxRead> {
  const response = await request(`/api/sessions?limit=${SESSION_PAGE}&offset=0`);
  if (!response.ok) {
    throw new FeedError(response.status === 401 ? "This phone's key for this machine stopped working." : `It answered ${response.status}.`);
  }
  const sessions = await response.json() as SessionListItem[];
  const asks = await readAsks(request, sessions.filter((session) => session.sessionStatus === "waiting_input"));
  return { sessions, asks };
}

async function readAsks(request: FeedRequest, waiting: readonly SessionListItem[]): Promise<Record<string, InboxAsk>> {
  const asks: Record<string, InboxAsk> = {};
  await Promise.all(waiting.slice(0, 12).map(async (session) => {
    const ask = await readAsk(request, session.session.id).catch(() => null);
    if (ask) asks[session.session.id] = ask;
  }));
  return asks;
}

async function readAsk(request: FeedRequest, sessionId: string): Promise<InboxAsk | null> {
  const permissions = await request(`/api/sessions/${encodeURIComponent(sessionId)}/permissions`);
  if (permissions.ok) {
    const list = (await permissions.json() as unknown[]).map(toPermissionAsk).filter((ask): ask is PermissionAsk => ask !== null);
    if (list.length) return { kind: "permission", ask: list[0] };
  }

  // No permission ask: look for a question the agent is waiting on in the last few messages.
  const messages = await request(`/api/sessions/${encodeURIComponent(sessionId)}/messages?limit=6`);
  if (!messages.ok) return null;
  const body = await messages.json() as { messages?: FleetMessage[] };
  return findPendingQuestion(body.messages ?? []);
}

/** The newest question tool call still waiting for an answer, from Fleet's message shape. */
export function findPendingQuestion(messages: readonly FleetMessage[]): InboxAsk | null {
  const pending = pendingQuestion(messages.map(convertFleetMessageToAccumulated));
  return pending ? { kind: "question", requestId: pending.requestId, question: pending.question, more: pending.more } : null;
}
