import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { FeedError, MachineFeed, POLL_INTERVAL_MS, SAFETY_REFRESH_MS, type FeedHub, type FeedRequest, type FeedSnapshot } from "../machine-feed";

class FakeHub implements FeedHub {
  handlers: Record<string, (...args: unknown[]) => void> = {};
  state = "Disconnected";
  stops = 0;
  constructor(private readonly startGate: Promise<void> = Promise.resolve()) {}
  async start() {
    await this.startGate;
    this.state = "Connected";
  }
  async stop() {
    this.stops += 1;
    this.state = "Disconnected";
  }
  async invoke() {
    return undefined;
  }
  on(method: string, handler: (...args: unknown[]) => void) {
    this.handlers[method] = handler;
  }
  onclose() {}
}

interface Count {
  count: number;
}

describe("MachineFeed", () => {
  let reads: number;
  let snapshots: FeedSnapshot<Count>[];
  let read: (request: FeedRequest) => Promise<Count>;

  beforeEach(() => {
    vi.useFakeTimers();
    reads = 0;
    snapshots = [];
    read = async () => ({ count: ++reads });
  });

  afterEach(() => vi.useRealTimers());

  function feed(hub: FeedHub = new FakeHub(), fetcher?: typeof fetch) {
    return new MachineFeed<Count>({
      target: { machineId: "mini", baseUrl: "http://mini.example.test:2113", token: "mini-token" },
      initial: { count: 0 },
      read: (request) => read(request),
      onChange: (snapshot) => snapshots.push(snapshot),
      createHub: () => hub,
      fetcher,
      now: () => 1000,
    });
  }

  it("holds what the caller's read returns, next to how the feed is doing", async () => {
    const f = feed();
    expect(f.state).toEqual({ count: 0, status: "connecting", lastHeardAt: null, error: null });

    await f.start();

    expect(f.state).toEqual({ count: 1, status: "live", lastHeardAt: 1000, error: null });
    await f.stop();
  });

  it("gives the read a request with the machine's key and no cookies", async () => {
    const fetcher = vi.fn(async () => new Response("[]")) as unknown as typeof fetch;
    read = async (request) => {
      await request("/api/sessions");
      return { count: 1 };
    };
    const f = feed(new FakeHub(), fetcher);

    await f.start();

    const [url, init] = vi.mocked(fetcher).mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("http://mini.example.test:2113/api/sessions");
    expect(init.credentials).toBe("omit");
    expect(new Headers(init.headers).get("Authorization")).toBe("Bearer mini-token");
    await f.stop();
  });

  it("marks the machine unreachable when the read fails, keeping the last read", async () => {
    const f = feed();
    await f.start();

    read = async () => {
      throw new FeedError("mini answered 500.");
    };
    await f.refresh();
    expect(f.state).toMatchObject({ count: 1, status: "unreachable", error: "mini answered 500.", lastHeardAt: 1000 });

    read = async () => {
      throw new TypeError("Failed to fetch");
    };
    await f.refresh();
    expect(f.state).toMatchObject({ count: 1, status: "unreachable", error: null });
    await f.stop();
  });

  it("reads once for a burst of events, and again for an event that came in during a read", async () => {
    const hub = new FakeHub();
    const f = feed(hub);
    await f.start();

    hub.handlers.Event("sessions", 1, {});
    hub.handlers.Event("sessions", 2, {});
    hub.handlers.Event("session:other", 3, {});
    await vi.advanceTimersByTimeAsync(300);
    expect(reads).toBe(2);

    let finish!: () => void;
    read = () => new Promise((resolve) => {
      finish = () => resolve({ count: ++reads });
    });
    void f.refresh();
    hub.handlers.Event("sessions", 4, {});
    await vi.advanceTimersByTimeAsync(300);
    expect(reads).toBe(2);

    read = async () => ({ count: ++reads });
    finish();
    await vi.advanceTimersByTimeAsync(0);
    expect(reads).toBe(4);
    await f.stop();
  });

  it("only reads on the safety refresh while the hub is live", async () => {
    const f = feed();
    await f.start();

    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 3);
    expect(reads).toBe(1);

    await vi.advanceTimersByTimeAsync(SAFETY_REFRESH_MS - POLL_INTERVAL_MS * 3);
    expect(reads).toBe(2);
    await f.stop();
  });

  it("closes a hub that finished connecting after the feed stopped, and reads no more", async () => {
    let open!: () => void;
    const hub = new FakeHub(new Promise((resolve) => {
      open = resolve;
    }));
    const f = feed(hub);
    const starting = f.start();
    await vi.advanceTimersByTimeAsync(0);

    await f.stop();
    open();
    await starting;

    expect(hub.stops).toBe(1);
    await vi.advanceTimersByTimeAsync(SAFETY_REFRESH_MS * 2);
    expect(reads).toBe(1);
  });
});
