/**
 * One machine for mission control: what the phone's inbox reads (its sessions, and what the waiting ones wait on) and
 * each session's "now" line, kept live by a {@link MachineFeed}. No Vue.
 */
import { MachineFeed, type FeedRequest, type FeedSnapshot, type MachineFeedOptions } from "@/lib/machine-feed";
import { readInbox, type InboxRead } from "@/lib/phone/inbox-feed";
import type { SessionNow } from "@/lib/mission-control";

export interface MissionRead extends InboxRead {
  now: Record<string, SessionNow>;
}

export type MissionSnapshot = FeedSnapshot<MissionRead>;

export type MissionFeedOptions = Omit<MachineFeedOptions<MissionRead>, "initial" | "read">;

export class MissionFeed extends MachineFeed<MissionRead> {
  constructor(options: MissionFeedOptions) {
    super({ ...options, initial: { sessions: [], asks: {}, now: {} }, read: readMission });
  }
}

async function readMission(request: FeedRequest): Promise<MissionRead> {
  const [inbox, now] = await Promise.all([readInbox(request), readNow(request)]);
  return { ...inbox, now };
}

/** The first call turns the machine's bookkeeping on, so it answers empty once. A machine too old to know it says 404: no lines, no error. */
async function readNow(request: FeedRequest): Promise<Record<string, SessionNow>> {
  try {
    const response = await request("/api/sessions/now");
    return response.ok ? await response.json() as Record<string, SessionNow> : {};
  } catch {
    return {};
  }
}
