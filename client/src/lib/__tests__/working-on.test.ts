import { describe, expect, it } from "vitest";
import { workingOn, workingOnText } from "@/lib/working-on";
import type { AccumulatedMessage } from "@/lib/client-types";

const NOW = Date.parse("2026-11-13T10:12:00Z");

function tool(id: string, name: string, status: string, startedMinutesAgo: number): AccumulatedMessage {
  return {
    messageId: `m-${id}`,
    sessionId: "s",
    role: "assistant",
    parts: [{ partId: id, type: "tool", tool: name, callId: id, state: { status, input: {}, time: { start: NOW - startedMinutesAgo * 60_000 } } }],
  };
}
function user(id: string, steered: boolean): AccumulatedMessage {
  return { messageId: id, sessionId: "s", role: "user", parts: [], createdAt: NOW, steered };
}

describe("workingOn", () => {
  it("is null with nothing running", () => {
    expect(workingOn([user("u1", false), tool("a", "bash", "completed", 3)], "next-step")).toBeNull();
  });

  it("names the one running tool and how long it has run", () => {
    const result = workingOn([user("u1", false), tool("a", "bash", "running", 10)], "next-step");
    expect(result).toEqual({ name: "Bash", since: NOW - 600_000, more: 0, held: false });
    expect(workingOnText(result!, NOW)).toBe("Bash running 10m");
  });

  it("names the longest-running one and counts the rest", () => {
    const result = workingOn([tool("a", "bash", "running", 2), tool("b", "webfetch", "running", 8), tool("c", "grep", "running", 1)], undefined);
    expect(result).toMatchObject({ name: "Web Fetch", more: 2 });
    expect(workingOnText(result!, NOW)).toBe("Web Fetch running 8m +2 more");
  });

  it("says the steered message waits when it sits below the running call on a next-step harness", () => {
    const messages = [user("u1", false), tool("a", "bash", "running", 10), user("u2", true)];
    const result = workingOn(messages, "next-step");
    expect(result?.held).toBe(true);
    expect(workingOnText(result!, NOW)).toBe("Bash running 10m. Your message goes to the agent when it finishes.");
  });

  it("says nothing extra on a harness that reads a steer at once, or one that doesn't say", () => {
    const messages = [tool("a", "bash", "running", 10), user("u2", true)];
    expect(workingOn(messages, "immediate")?.held).toBe(false);
    expect(workingOn(messages, null)?.held).toBe(false);
  });

  it("doesn't hold a steered message that was sent before the call started", () => {
    const messages = [user("u2", true), tool("a", "bash", "running", 10)];
    expect(workingOn(messages, "next-step")?.held).toBe(false);
  });

  it("doesn't hold a message that isn't steered", () => {
    expect(workingOn([tool("a", "bash", "running", 10), user("u2", false)], "next-step")?.held).toBe(false);
  });

  it("falls back to the message's start when the call has none", () => {
    const message = tool("a", "bash", "running", 0);
    (message.parts[0] as { state: unknown }).state = { status: "running", input: {} };
    message.createdAt = NOW - 4 * 60_000;
    expect(workingOn([message], undefined)?.since).toBe(NOW - 4 * 60_000);
  });
});
