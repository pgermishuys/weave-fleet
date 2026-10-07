/**
 * Where a harness compacted the conversation, as the conversation shows it: a divider ("Context compacted · 181k → 34k
 * tokens") with the summary the model goes on from behind Show summary. Every harness the same way: Claude Code and
 * OpenCode 2 put the summary on the divider; OpenCode writes it as a message of its own, which is folded in here.
 */

import type { AccumulatedMessage } from "@/lib/client-types";
import { formatTokens } from "@/lib/context-usage";

export interface CompactionView {
  /** "auto" or "manual", when the harness says. */
  trigger?: string;
  tokensBefore?: number;
  tokensAfter?: number;
  summary?: string;
}

/** "Context compacted · 181k → 34k tokens", or just "Context compacted" when the harness gives no sizes. */
export function describeCompaction(compaction: CompactionView): string {
  const { tokensBefore, tokensAfter } = compaction;
  if (tokensBefore != null && tokensAfter != null) {
    return `Context compacted · ${formatTokens(tokensBefore)} → ${formatTokens(tokensAfter)} tokens`;
  }
  if (tokensBefore != null) {
    return `Context compacted · ${formatTokens(tokensBefore)} tokens before`;
  }
  return "Context compacted";
}

/** The message's compaction divider, if it is one. */
export function compactionOf(message: AccumulatedMessage): CompactionView | undefined {
  const part = message.parts.find((candidate) => candidate.type === "compaction");
  if (!part || part.type !== "compaction") return undefined;
  return {
    trigger: part.trigger,
    tokensBefore: part.tokensBefore,
    tokensAfter: part.tokensAfter,
    summary: part.summary,
  };
}

/** A summary message's text: what the compaction wrote. */
export function summaryText(message: AccumulatedMessage): string {
  return message.parts
    .filter((part) => part.type === "text")
    .map((part) => (part.type === "text" ? part.text : ""))
    .join("\n\n")
    .trim();
}

/**
 * Folds the summaries a harness wrote as messages of their own into the divider before them, so the conversation shows
 * the compaction once. Returns, by message id, the summary each divider gets, and the ids of the summary messages,
 * which aren't shown. A summary with no divider before it (the divider is on an older page) stays a message.
 */
export function foldCompactionSummaries(messages: readonly AccumulatedMessage[]): {
  summaries: Map<string, string>;
  hidden: Set<string>;
} {
  const summaries = new Map<string, string>();
  const hidden = new Set<string>();
  let divider: { id: string; hasSummary: boolean } | null = null;
  for (const message of messages) {
    const compaction = compactionOf(message);
    if (compaction) {
      divider = { id: message.messageId, hasSummary: Boolean(compaction.summary) };
      continue;
    }
    if (!message.compactionSummary || !divider) continue;
    hidden.add(message.messageId);
    const text = summaryText(message);
    if (!divider.hasSummary && text) summaries.set(divider.id, text);
    divider = null;
  }
  return { summaries, hidden };
}
