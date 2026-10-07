import { describe, expect, it } from "vitest";
import type { AccumulatedMessage } from "@/lib/client-types";
import { describeCompaction, foldCompactionSummaries } from "@/lib/compaction";

function message(id: string, overrides: Partial<AccumulatedMessage> = {}): AccumulatedMessage {
  return { messageId: id, sessionId: "s1", role: "assistant", parts: [], ...overrides };
}

const divider = (id: string, summary?: string) =>
  message(id, { parts: [{ partId: `${id}-c`, type: "compaction", trigger: "auto", summary }] });
const summary = (id: string, text: string) =>
  message(id, { compactionSummary: true, parts: [{ partId: `${id}-t`, type: "text", text }] });

describe("compaction", () => {
  it("says how big the context was before and after, when the harness gives the sizes", () => {
    expect(describeCompaction({ tokensBefore: 181_000, tokensAfter: 34_000 })).toBe("Context compacted · 181k → 34k tokens");
    expect(describeCompaction({ tokensBefore: 35_438 })).toBe("Context compacted · 35.4k tokens before");
    expect(describeCompaction({})).toBe("Context compacted");
  });

  // OpenCode writes its summary as an assistant message of its own: it goes behind the divider, not shown twice.
  it("folds a summary message into the divider before it", () => {
    const folded = foldCompactionSummaries([message("m1"), divider("m2"), summary("m3", "What we did."), message("m4")]);
    expect(folded.summaries.get("m2")).toBe("What we did.");
    expect([...folded.hidden]).toEqual(["m3"]);
  });

  it("keeps a divider's own summary, and still hides the message", () => {
    const folded = foldCompactionSummaries([divider("m1", "Own summary"), summary("m2", "Other")]);
    expect(folded.summaries.has("m1")).toBe(false);
    expect(folded.hidden.has("m2")).toBe(true);
  });

  it("shows a summary whose divider is on an older page as it is", () => {
    const folded = foldCompactionSummaries([summary("m1", "Orphan"), message("m2")]);
    expect(folded.hidden.size).toBe(0);
  });
});
