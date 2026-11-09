/**
 * Reads that follow each other read as one row ("Read 8 files") even when the agent made them in separate steps, which
 * the harness delivers as separate messages. Only the tool rows are joined: a message with any text, thinking, image or
 * question of its own between two reads keeps them apart, since it is something the reader should see in between.
 */
import type { ActivityMessage } from "@/composables/use-activity-messages";
import type { ToolCardItem } from "@/components/session/activity-stream-tool-card";
import { isReadCall } from "@/lib/tool-row";

const isDoneRead = (tool: ToolCardItem): boolean => !tool.delegation && isReadCall(tool.kind) && tool.status === "Completed";

/** The message shows nothing above its calls: no text, thinking, image, or anything else drawn before them. */
function startsWithCalls(message: ActivityMessage): boolean {
  return message.role === "assistant"
    && message.body.trim().length === 0
    && message.images.length === 0
    && !(message.reasoningParts?.some((part) => part.text.trim() || part.summary?.trim()))
    && !message.peer && !message.background && !message.compaction && !message.shell && !message.command
    && (message.tools?.length ?? 0) > 0;
}

/** The message shows nothing after its calls either. */
function endsWithCalls(message: ActivityMessage): boolean {
  return (message.questionParts?.length ?? 0) === 0 && !message.turnError;
}

// A joined list is kept per pair of lists, so an unchanged message hands its bubble the same array it had.
const joined = new WeakMap<readonly ToolCardItem[], WeakMap<readonly ToolCardItem[], ToolCardItem[]>>();
function join(left: ToolCardItem[], right: ToolCardItem[], count: number): ToolCardItem[] {
  let byRight = joined.get(left);
  if (!byRight) joined.set(left, (byRight = new WeakMap()));
  let both = byRight.get(right);
  if (!both) byRight.set(right, (both = [...left, ...right.slice(0, count)]));
  return both;
}

export function mergeReadRuns<T extends ActivityMessage>(messages: readonly T[]): T[] {
  const out: T[] = [];
  for (const message of messages) {
    const previous = out.at(-1);
    const tools = message.tools ?? [];
    if (previous && startsWithCalls(message) && previous.role === "assistant" && endsWithCalls(previous)
      && previous.tools?.length && isDoneRead(previous.tools.at(-1)!) && isDoneRead(tools[0]!)) {
      // Take the run of reads off the front of this message.
      let count = 0;
      while (count < tools.length && isDoneRead(tools[count]!)) count += 1;
      const rest = tools.slice(count);
      const merged = { ...previous, tools: join(previous.tools, tools, count) };
      out[out.length - 1] = merged;
      const hasOtherContent = rest.length > 0 || (message.questionParts?.length ?? 0) > 0 || message.turnError != null;
      if (hasOtherContent) out.push({ ...message, tools: rest });
      continue;
    }
    out.push(message);
  }
  return out;
}
