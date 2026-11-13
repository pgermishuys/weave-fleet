/**
 * What "Working" is waiting on: the tool call that is running, named as its row names it, with its own time. When your
 * message sent into the turn (steered) sits below that call and the harness reads a steer at its next step, it says
 * the message waits. Harness-neutral: the delivery comes from the harness's `steerDelivery` capability.
 */
import type { AccumulatedMessage } from "@/lib/client-types";
import { toolStatus } from "@/components/session/activity-stream-tool-card";
import { formatElapsed } from "@/lib/running-work";
import { getTool } from "@/lib/tools";

export interface WorkingOn {
  /** The longest-running call's name, as its row shows it (Bash, Web Fetch). */
  name: string;
  /** When it started, in epoch milliseconds; null when the harness gave no start. */
  since: number | null;
  /** How many other calls run besides it. */
  more: number;
  /** Your steered message sits below the call and the harness reads it only when the call finishes. */
  held: boolean;
}

function startedAt(state: unknown): number | null {
  const time = (state as { time?: { start?: unknown } } | null)?.time;
  return typeof time?.start === "number" ? time.start : null;
}

export function workingOn(messages: readonly AccumulatedMessage[], steerDelivery: string | null | undefined): WorkingOn | null {
  const running: { name: string; since: number | null; index: number }[] = [];
  messages.forEach((message, index) => {
    if (message.role !== "assistant") return;
    for (const part of message.parts ?? []) {
      if (part.type !== "tool" || toolStatus(part) !== "running") continue;
      // A harness that keeps no start on the call: the message that holds it began about then.
      const since = startedAt(part.state) ?? message.createdAt ?? null;
      running.push({ name: getTool(part.tool).heading ?? part.tool, since, index });
    }
  });
  if (running.length === 0) return null;

  const first = running.reduce((best, item) => ((item.since ?? Infinity) < (best.since ?? Infinity) ? item : best));
  const held = steerDelivery === "next-step"
    && messages.some((message, index) => index > first.index && message.role === "user" && message.steered === true);
  return { name: first.name, since: first.since, more: running.length - 1, held };
}

/** "Bash running 10m", "Bash running 10m +2 more", and the held sentence after it. */
export function workingOnText(work: WorkingOn, now: number): string {
  const time = work.since === null ? "" : ` ${formatElapsed(now - work.since)}`;
  const line = `${work.name} running${time}${work.more > 0 ? ` +${work.more} more` : ""}`;
  return work.held ? `${line}. Your message goes to the agent when it finishes.` : line;
}
