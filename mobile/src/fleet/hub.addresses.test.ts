import { describe, expect, test } from "bun:test";
import type { HubConnection } from "@microsoft/signalr";
import { MachineHub } from "./hub";

/** A stand-in for SignalR's connection: it starts (or fails to) and records what was invoked on it. */
class FakeConnection {
  state = "Disconnected";
  invoked: string[] = [];
  stopped = false;
  private handlers: Record<string, (...args: unknown[]) => void> = {};
  constructor(readonly url: string, private holds: boolean) {}
  on(name: string, handler: (...args: unknown[]) => void) {
    this.handlers[name] = handler;
  }
  onreconnecting() {}
  onreconnected() {}
  onclose() {}
  async start() {
    if (!this.holds) throw new Error("WebSocket failed to connect");
    this.state = "Connected";
  }
  async stop() {
    this.stopped = true;
    this.state = "Disconnected";
  }
  async invoke(method: string, ...args: unknown[]) {
    this.invoked.push(args.length ? `${method}(${args.join(",")})` : method);
    return method === "SubscribeToSessionAsync" ? { session: { title: `from ${this.url}` } } : undefined;
  }
  emit(topic: string, data: unknown) {
    this.handlers.Event?.(topic, 1, data);
  }
}

class TestHub extends MachineHub {
  built: FakeConnection[] = [];
  holds = new Set<string>();
  protected connectTo(url: string): HubConnection {
    const connection = new FakeConnection(url, this.holds.has(url));
    this.built.push(connection);
    return connection as unknown as HubConnection;
  }
}

const LAN = "http://192.168.1.42:2113";
const TS = "https://studio-desktop.tail4f2a.ts.net";

async function connectedOnLan() {
  let lives = 0;
  const hub = new TestHub({ baseUrl: LAN, token: "key" }, () => {}, () => lives++);
  hub.holds.add(LAN);
  hub.start();
  while (hub.state !== "connected") await new Promise((r) => setTimeout(r, 1));
  return { hub, lives: () => lives };
}

describe("moving the hub to a better address", () => {
  test("switches once the new connection is up and subscribed, with fresh snapshots, then closes the old one", async () => {
    const { hub, lives } = await connectedOnLan();
    const titles: string[] = [];
    hub.subscribeSession("s1", (snapshot) => titles.push(snapshot.session.title), () => {});
    hub.holds.add(TS);

    expect(await hub.moveTo(TS)).toBe(true);
    const [lan, ts] = hub.built;
    expect(ts.url).toBe(TS);
    expect(ts.invoked).toEqual(["SubscribeToSessionsTopicAsync", "SubscribeToSessionsTopicAsync", "SubscribeToSessionAsync(s1)"]);
    expect(titles.at(-1)).toBe(`from ${TS}`);
    expect(lan.stopped).toBe(true);
    expect(hub.state).toBe("connected");
    expect(lives()).toBe(2);
  });

  test("stays on the working connection when the better address won't hold one", async () => {
    const { hub } = await connectedOnLan();
    expect(await hub.moveTo(TS)).toBe(false);
    const [lan] = hub.built;
    expect(lan.stopped).toBe(false);
    expect(hub.state).toBe("connected");
  });

  test("the old connection's events stop counting once it's been replaced", async () => {
    const { hub } = await connectedOnLan();
    const heard: string[] = [];
    hub.subscribeSessions((event) => heard.push(event.type));
    hub.holds.add(TS);
    await hub.moveTo(TS);
    const [lan, ts] = hub.built;
    lan.emit("sessions", { type: "session.created", properties: {} });
    ts.emit("sessions", { type: "session.updated", properties: {} });
    expect(heard).toEqual(["session.updated"]);
  });
});
