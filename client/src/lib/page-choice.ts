/**
 * "Choose on a page": the agent shows a page (`fleet_page_show` with `waitForChoice`) and waits while the user picks.
 * The page only proposes a pick; Fleet's own bar under the page sends it. The server keeps the outcome on the canvas.
 */
import { asRecord } from "@/lib/tools/input";

/** The user preference behind the switch in Settings → Labs. */
export const PAGE_CHOICE_PREFERENCE_KEY = "PageChoice";

export interface PageChoice {
  callId: string;
  status: "waiting" | "answered" | "not-answered";
  value?: string;
  summary?: string;
  /** Why it went unanswered: "skipped", "wrote instead", "stopped", "Fleet restarted" or "timed out". */
  reason?: string;
  at: string;
}

/** The choice in a page canvas's state, or undefined for anything malformed. */
export function readPageChoice(value: unknown): PageChoice | undefined {
  const record = asRecord(value);
  if (!record) return undefined;
  const { callId, status, at } = record;
  if (typeof callId !== "string" || !callId || typeof at !== "string") return undefined;
  if (status !== "waiting" && status !== "answered" && status !== "not-answered") return undefined;
  const text = (item: unknown) => (typeof item === "string" && item ? item : undefined);
  return { callId, status, value: text(record.value), summary: text(record.summary), reason: text(record.reason), at };
}

/** A waiting choice is live while the session is in a turn; a Fleet restart leaves "waiting" behind with the session idle. */
export function isChoiceLive(choice: PageChoice | undefined, activityStatus: string | undefined): boolean {
  return choice?.status === "waiting" && !!activityStatus && activityStatus !== "idle";
}

export interface PageChoiceBarState {
  kind: "waiting" | "proposed" | "answered" | "not-answered";
  text: string;
  /** Bold part of the sentence, e.g. the picked summary. */
  strong?: string;
  after?: string;
  at?: string;
}

const NOT_ANSWERED: Record<string, string> = {
  skipped: "Not answered. You skipped it.",
  "wrote instead": "Not answered. You wrote instead.",
  stopped: "Not answered. The turn stopped.",
  "Fleet restarted": "Not answered. Fleet restarted.",
  "timed out": "Not answered after 24 hours.",
};

/** What the bar under the page says, from the canvas's choice, whether it's live and what the page proposed. */
export function pageChoiceBar(
  choice: PageChoice,
  live: boolean,
  proposal: { value: string; summary: string } | null,
): PageChoiceBarState {
  if (choice.status === "answered") {
    return { kind: "answered", text: "Sent: ", strong: choice.summary ?? choice.value ?? "", after: ".", at: choice.at };
  }
  if (choice.status === "waiting" && live) {
    return proposal
      ? { kind: "proposed", text: "You picked ", strong: proposal.summary, after: "." }
      : { kind: "waiting", text: "The agent is waiting for your pick." };
  }
  const text = (choice.status === "not-answered" && choice.reason && NOT_ANSWERED[choice.reason]) || "Not answered.";
  return { kind: "not-answered", text, at: choice.at };
}

/** The row's title for a `fleet_page_show` call that waits for a pick, or undefined for any other call. */
export function pageChoiceRowTitle(
  input: Record<string, unknown> | null,
  status: string | undefined,
  metadata: Record<string, unknown> | null,
): string | undefined {
  if (input?.waitForChoice !== true) return undefined;
  if (status === "error") return "Not answered";
  if (status === "completed") {
    const choice = asRecord(metadata?.choice);
    if (choice?.answered === true) {
      const picked = typeof choice.summary === "string" && choice.summary ? choice.summary : String(choice.value ?? "");
      return `Picked ${picked}`.trim();
    }
    const reason = typeof choice?.reason === "string" ? choice.reason : "";
    const why = reason === "skipped" ? "you skipped it" : reason;
    return why ? `Not picked · ${why}` : "Not picked";
  }
  const title = typeof input.title === "string" ? input.title : "";
  return title ? `Waiting for your pick · ${title}` : "Waiting for your pick";
}
