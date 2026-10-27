import { describe, expect, it } from "vitest";
import { peekEvents } from "@/lib/mission-peek";
import { nudgeRequest } from "@/lib/mission-control-requests";
import type { FleetMessage } from "@/lib/pagination-utils";

const message = (id: string, role: string, parts: FleetMessage["parts"]): FleetMessage =>
  ({ id, role, parts, timestamp: "2026-10-27T12:00:00Z", textContent: "" });

describe("the peek's events", () => {
  it("reads tool calls and replies of the assistant, oldest first, and leaves the user's words out", () => {
    const events = peekEvents([
      message("m1", "user", [{ type: "text", kind: 0, text: "please fix it" }]),
      message("m2", "assistant", [
        { type: "tool", kind: 1, toolName: "Read", arguments: { filePath: "src/webhooks/retry.ts" } },
        { type: "tool", kind: 1, toolName: "bash", arguments: { command: "pnpm test webhooks\nsecond line" } },
        { type: "text", kind: 0, text: "Found three retry sites.\nMore." },
      ]),
    ]);

    expect(events.map((event) => event.text)).toEqual(["read · src/webhooks/retry.ts", "bash · pnpm test webhooks", "Found three retry sites."]);
  });

  it("keeps only the newest few", () => {
    const parts = Array.from({ length: 9 }, (_, index) => ({ type: "tool", kind: 1, toolName: "read", arguments: { path: `f${index}` } }));

    expect(peekEvents([message("m", "assistant", parts)], 3).map((event) => event.text)).toEqual(["read · f6", "read · f7", "read · f8"]);
  });
});

describe("a nudge", () => {
  const target = { baseUrl: "http://mini:5732", token: "t" };
  const bodyOf = (request: { init: RequestInit }) => JSON.parse(request.init.body as string) as Record<string, unknown>;

  it("goes into the running turn where the harness steers", () => {
    const request = nudgeRequest(target, "s1", "also cover timeouts", true, true);

    expect(request.url).toBe("http://mini:5732/api/sessions/s1/prompt");
    expect(bodyOf(request)).toMatchObject({ text: "also cover timeouts", delivery: "steer" });
    expect((request.init.headers as Record<string, string>).Authorization).toBe("Bearer t");
  });

  it("waits in the queue where it doesn't", () => {
    const request = nudgeRequest(target, "s1", "also cover timeouts", true, false);

    expect(request.url).toBe("http://mini:5732/api/sessions/s1/queue");
    expect(bodyOf(request)).toEqual({ text: "also cover timeouts" });
  });

  it("is just a prompt for a session that isn't working", () => {
    const request = nudgeRequest({ baseUrl: "", token: null }, "s1", "go on", false, false);

    expect(request.url).toBe("/api/sessions/s1/prompt");
    expect(bodyOf(request)).not.toHaveProperty("delivery");
  });
});
