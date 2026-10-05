import { describe, expect, it } from "vitest";
import type { PermissionAsk } from "@/composables/use-session-permissions";
import type { AccumulatedMessage } from "@/lib/client-types";
import { chooseDock, pendingQuestion } from "../dock-state";

const ask = (id: string, askedAt: string): PermissionAsk => ({ id, sessionId: "s", kind: "shell", tool: "bash", title: id, always: [], askedAt });
const question = { requestId: "call-q", question: { header: "h", question: "Which?", options: [] }, more: 0 };

describe("chooseDock", () => {
  it("is the composer when nothing waits", () => {
    expect(chooseDock({ permissions: [], question: null, later: new Set() })).toEqual({ kind: "composer", later: 0 });
  });

  it("puts the oldest permission first, before a question", () => {
    const dock = chooseDock({ permissions: [ask("new", "2026-10-05T14:05:00Z"), ask("old", "2026-10-05T14:01:00Z")], question, later: new Set() });
    expect(dock).toMatchObject({ kind: "permission", ask: { id: "old" } });
  });

  it("shows the question once the permissions are answered", () => {
    expect(chooseDock({ permissions: [], question, later: new Set() })).toMatchObject({ kind: "question", pending: { requestId: "call-q" } });
  });

  it("steps an ask aside when put off, and counts it", () => {
    const dock = chooseDock({ permissions: [ask("a", "1")], question, later: new Set(["a"]) });
    expect(dock).toMatchObject({ kind: "question", later: 1 });
    expect(chooseDock({ permissions: [ask("a", "1")], question, later: new Set(["a", "call-q"]) })).toEqual({ kind: "composer", later: 2 });
  });

  it("puts the ask a notification opened first", () => {
    const dock = chooseDock({ permissions: [ask("old", "1"), ask("tapped", "2")], question: null, later: new Set(), focus: "tapped" });
    expect(dock).toMatchObject({ kind: "permission", ask: { id: "tapped" } });
  });
});

describe("pendingQuestion", () => {
  const message = (status: string): AccumulatedMessage => ({
    messageId: "m", sessionId: "s", role: "assistant",
    parts: [{ partId: "p", type: "tool", tool: "question", callId: "call-q", state: { status, input: { questions: [{ header: "h", question: "Which?", options: [] }, { header: "h2", question: "And?", options: [] }] } } }],
  });

  it("finds a question still waiting", () => {
    expect(pendingQuestion([message("running")])).toEqual({ requestId: "call-q", question: { header: "h", question: "Which?", options: [] }, more: 1 });
    expect(pendingQuestion([message("completed")])).toBeNull();
  });
});
