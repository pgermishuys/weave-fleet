import { describe, expect, it } from "vitest";
import { mergeReadRuns } from "@/lib/merge-reads";
import type { ActivityMessage } from "@/composables/use-activity-messages";

const tool = (id: string, kind = "read", status = "Completed") => ({ id, title: id, kind, status });
function msg(id: string, tools: unknown[], extra: Partial<ActivityMessage> = {}): ActivityMessage {
  return { id, role: "assistant", author: "", senderKey: "a", body: "", images: [], tools, clusterPosition: "single", showIdentity: true, ...extra } as ActivityMessage;
}

describe("mergeReadRuns", () => {
  it("joins reads across messages into the first message's tools", () => {
    const out = mergeReadRuns([msg("a", [tool("1"), tool("2")]), msg("b", [tool("3")]), msg("c", [tool("4"), tool("5")])]);
    expect(out).toHaveLength(1);
    expect(out[0]!.tools!.map((t) => t.id)).toEqual(["1", "2", "3", "4", "5"]);
  });

  it("keeps messages apart when text, thinking or another call sits between", () => {
    expect(mergeReadRuns([msg("a", [tool("1")]), msg("b", [tool("2")], { body: "Now this." })])).toHaveLength(2);
    expect(mergeReadRuns([msg("a", [tool("1")]), msg("b", [tool("2")], { reasoningParts: [{ partId: "r", type: "reasoning", text: "hm" }] })])).toHaveLength(2);
    expect(mergeReadRuns([msg("a", [tool("1"), tool("x", "edit")]), msg("b", [tool("2")])])).toHaveLength(2);
  });

  it("moves only the leading reads of the next message, and keeps its other calls", () => {
    const out = mergeReadRuns([msg("a", [tool("1")]), msg("b", [tool("2"), tool("3", "edit")])]);
    expect(out.map((m) => m.tools!.map((t) => t.id))).toEqual([["1", "2"], ["3"]]);
  });

  it("joins a read that is still running, but not one that failed", () => {
    expect(mergeReadRuns([msg("a", [tool("1")]), msg("b", [tool("2", "read", "Running")])])).toHaveLength(1);
    expect(mergeReadRuns([msg("a", [tool("1")]), msg("b", [tool("2", "read", "Error")])])).toHaveLength(2);
  });

  it("hands back the same array for the same messages", () => {
    const a = msg("a", [tool("1")]);
    const b = msg("b", [tool("2")]);
    expect(mergeReadRuns([a, b])[0]!.tools).toBe(mergeReadRuns([a, b])[0]!.tools);
  });
});
