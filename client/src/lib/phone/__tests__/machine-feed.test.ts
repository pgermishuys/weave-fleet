import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { findPendingQuestion, MachineFeed, POLL_INTERVAL_MS, type FeedHub, type FeedSnapshot, type FeedTarget } from "../machine-feed";

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function listItem(id: string, status: string) {
  return { session: { id, title: id, time: { created: 1, updated: 2 } }, instanceId: "i", sessionStatus: status, activityStatus: "idle", retentionStatus: "active", parentSessionId: null };
}

const questionMessage = {
  id: "m1",
  role: "assistant",
  timestamp: "2026-10-05T14:00:00Z",
  textContent: "",
  parts: [{
    type: "tool", kind: 1, toolName: "question", toolCallId: "call-q", state: 1,
    arguments: { questions: [{ header: "401", question: "Plain 401 or a page?", options: [{ label: "Plain 401", description: "" }] }] },
  }],
};

class FakeHub implements FeedHub {
  handlers: Record<string, (...args: unknown[]) => void> = {};
  close: ((error?: Error) => void) | null = null;
  state = "Disconnected";
  constructor(private readonly fail = false) {}
  async start() {
    if (this.fail) throw new Error("no hub");
    this.state = "Connected";
  }
  async stop() {
    this.state = "Disconnected";
  }
  async invoke() {
    return undefined;
  }
  on(method: string, handler: (...args: unknown[]) => void) {
    this.handlers[method] = handler;
  }
  onclose(handler: (error?: Error) => void) {
    this.close = handler;
  }
}

describe("MachineFeed", () => {
  let routes: Record<string, (init: RequestInit) => Response>;
  let calls: { url: string; init: RequestInit }[];
  let snapshots: FeedSnapshot[];
  const fetcher = (async (input: RequestInfo | URL, init: RequestInit = {}) => {
    const url = String(input);
    calls.push({ url, init });
    const path = url.replace(/^https?:\/\/[^/]+/, "").split("?")[0];
    const route = routes[path];
    if (!route) throw new TypeError("Failed to fetch");
    return route(init);
  }) as typeof fetch;

  beforeEach(() => {
    vi.useFakeTimers();
    calls = [];
    snapshots = [];
    routes = {
      "/api/sessions": () => json([listItem("perm", "waiting_input"), listItem("q", "waiting_input"), listItem("w", "active")]),
      "/api/sessions/perm/permissions": () => json([{ id: "p1", sessionId: "perm", kind: "shell", tool: "bash", title: "dotnet test", always: [], askedAt: "2026-10-05T14:00:00Z" }]),
      "/api/sessions/q/permissions": () => json([]),
      "/api/sessions/q/messages": () => json({ messages: [questionMessage] }),
    };
  });

  afterEach(() => vi.useRealTimers());

  function feed(target: FeedTarget = { machineId: "falcon", baseUrl: "https://falcon.ts.net", token: "fdt_falcon.1" }, hub = new FakeHub(), onUnauthorized?: () => Promise<string | null>) {
    return { hub, feed: new MachineFeed({ target, fetcher, createHub: () => hub, onChange: (s) => snapshots.push(s), onUnauthorized, now: () => 1000 }) };
  }

  it("reads sessions and what the waiting ones wait on, with the phone's token and no cookies", async () => {
    const { feed: f } = feed();
    await f.start();

    expect(f.state.status).toBe("live");
    expect(f.state.sessions.map((s) => s.session.id)).toEqual(["perm", "q", "w"]);
    expect(f.state.asks.perm).toMatchObject({ kind: "permission", ask: { id: "p1", title: "dotnet test" } });
    expect(f.state.asks.q).toMatchObject({ kind: "question", requestId: "call-q", question: { question: "Plain 401 or a page?" } });
    expect(f.state.lastHeardAt).toBe(1000);
    for (const call of calls) {
      expect(new Headers(call.init.headers).get("Authorization")).toBe("Bearer fdt_falcon.1");
      expect(call.init.credentials).toBe("omit");
    }
    await f.stop();
  });

  it("uses the cookie for home", async () => {
    const { feed: f } = feed({ machineId: "home", baseUrl: "", token: null });
    await f.start();

    expect(calls[0].url).toBe("/api/sessions?limit=100&offset=0");
    expect(calls[0].init.credentials).toBe("include");
    expect(new Headers(calls[0].init.headers).has("Authorization")).toBe(false);
    await f.stop();
  });

  it("re-reads when the sessions topic says something changed", async () => {
    const { feed: f, hub } = feed();
    await f.start();
    const before = calls.length;

    hub.handlers.Event("sessions", 1, {});
    hub.handlers.Event("sessions", 2, {});
    await vi.advanceTimersByTimeAsync(400);

    expect(calls.filter((c) => c.url.includes("/api/sessions?")).length).toBe(2);
    expect(calls.length).toBeGreaterThan(before);
    await f.stop();
  });

  it("polls when the hub won't connect", async () => {
    const { feed: f } = feed(undefined, new FakeHub(true));
    await f.start();
    const reads = () => calls.filter((c) => c.url.includes("/api/sessions?")).length;
    expect(reads()).toBe(1);

    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS);

    expect(reads()).toBe(2);
    expect(f.state.status).toBe("polling");
    await f.stop();
  });

  it("keeps the last list when the machine stops answering", async () => {
    const { feed: f } = feed(undefined, new FakeHub(true));
    await f.start();
    delete routes["/api/sessions"];

    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS);

    expect(f.state.status).toBe("unreachable");
    expect(f.state.sessions).toHaveLength(3);
    expect(f.state.lastHeardAt).toBe(1000);
    await f.stop();
  });

  it("asks for a new key once when the machine turns the token away", async () => {
    routes["/api/sessions"] = (init) => new Headers(init.headers).get("Authorization") === "Bearer fdt_new.2"
      ? json([listItem("w", "active")])
      : json({}, 401);
    const renew = vi.fn(async () => "fdt_new.2");
    const { feed: f } = feed(undefined, new FakeHub(), renew);

    await f.start();

    expect(renew).toHaveBeenCalledTimes(1);
    expect(f.state.sessions.map((s) => s.session.id)).toEqual(["w"]);
    await f.stop();
  });
});

describe("findPendingQuestion", () => {
  it("ignores answered questions", () => {
    const answered = { ...questionMessage, parts: [{ ...questionMessage.parts[0], state: 2 }] };
    expect(findPendingQuestion([answered])).toBeNull();
    expect(findPendingQuestion([questionMessage])).toMatchObject({ requestId: "call-q", more: 0 });
  });
});
