import { describe, expect, it } from "vitest";
import { MissionFeed, type MissionSnapshot } from "@/lib/mission-control-feed";
import type { FeedHub } from "@/lib/machine-feed";

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

const noHub = (): FeedHub => ({
  start: async () => { throw new Error("no hub"); },
  stop: async () => undefined,
  invoke: async () => undefined,
  on: () => undefined,
  onclose: () => undefined,
  state: "Disconnected",
});

async function read(now: () => Response): Promise<MissionSnapshot> {
  const fetcher = (async (input: RequestInfo | URL) => {
    const path = String(input).split("?")[0];
    if (path === "/api/sessions") return json([{ session: { id: "s1", title: "s1" }, sessionStatus: "active", retentionStatus: "active" }]);
    if (path === "/api/sessions/now") return now();
    return json({}, 404);
  }) as typeof fetch;
  let latest!: MissionSnapshot;
  const feed = new MissionFeed({ target: { machineId: "home", baseUrl: "", token: null }, onChange: (snapshot) => { latest = snapshot; }, createHub: noHub, fetcher });
  await feed.refresh();
  await feed.stop();
  return latest;
}

describe("MissionFeed", () => {
  it("reads the sessions and each session's now line", async () => {
    const snapshot = await read(() => json({ s1: { text: "bash · pnpm test", kind: "tool", at: "2026-10-27T12:00:00Z" } }));

    expect(snapshot.sessions.map((item) => item.session.id)).toEqual(["s1"]);
    expect(snapshot.now.s1.text).toBe("bash · pnpm test");
    expect(snapshot.error).toBeNull();
  });

  it("shows no now lines and no error for a machine too old to know them", async () => {
    const snapshot = await read(() => json({}, 404));

    expect(snapshot.now).toEqual({});
    expect(snapshot.sessions).toHaveLength(1);
    expect(snapshot.status).not.toBe("unreachable");
    expect(snapshot.error).toBeNull();
  });
});
