import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  connectTerminal,
  MAX_FAILED_ATTEMPTS,
  RECONNECT_DELAYS_MS,
  type TerminalConnectionHandlers,
  type TerminalConnectionStatus,
} from "@/lib/terminal-socket";

vi.mock("@/lib/api-client", () => ({
  wsUrl: (path: string) => `ws://fleet.test${path}`,
  apiFetch: vi.fn(),
}));

class FakeSocket {
  static readonly OPEN = 1;
  readyState = 0;
  binaryType = "blob";
  sent: Array<string | Uint8Array> = [];
  closedWith: number | null = null;
  onopen: (() => void) | null = null;
  onmessage: ((event: MessageEvent) => void) | null = null;
  onclose: ((event: CloseEvent) => void) | null = null;

  constructor(readonly url: string) {}

  send(data: string | Uint8Array): void {
    this.sent.push(data);
  }

  close(code: number): void {
    this.closedWith = code;
  }

  open(): void {
    this.readyState = FakeSocket.OPEN;
    this.onopen?.();
  }

  output(text: string): void {
    this.onmessage?.({ data: new TextEncoder().encode(text).buffer } as MessageEvent);
  }

  control(message: object): void {
    this.onmessage?.({ data: JSON.stringify(message) } as MessageEvent);
  }

  drop(code: number): void {
    this.readyState = 3;
    this.onclose?.({ code } as CloseEvent);
  }
}

function setup() {
  const sockets: FakeSocket[] = [];
  const scheduled: Array<{ callback: () => void; ms: number }> = [];
  const events: string[] = [];
  const statuses: TerminalConnectionStatus[] = [];
  const decoder = new TextDecoder();
  const handlers: TerminalConnectionHandlers = {
    onReset: () => events.push("reset"),
    onOutput: (data) => events.push(`out:${decoder.decode(data)}`),
    onReady: () => events.push("ready"),
    onCleared: () => events.push("cleared"),
    onExit: (code) => events.push(`exit:${code}`),
    onStatus: (status) => statuses.push(status),
  };
  const connection = connectTerminal({
    sessionId: "s 1",
    terminalId: "t1",
    cols: 100,
    rows: 30,
    handlers,
    createSocket: (url) => {
      const socket = new FakeSocket(url);
      sockets.push(socket);
      return socket as unknown as WebSocket;
    },
    schedule: (callback, ms) => scheduled.push({ callback, ms }),
  });
  const latest = () => sockets[sockets.length - 1];
  const sentText = (socket: FakeSocket) => socket.sent.map((item) => (typeof item === "string" ? item : decoder.decode(item)));
  return { connection, sockets, scheduled, events, statuses, latest, sentText };
}

describe("connectTerminal", () => {
  beforeEach(() => {
    vi.stubGlobal("WebSocket", FakeSocket);
  });

  it("connects with the terminal's size, then gets the scrollback, ready, and live output", () => {
    const { sockets, events, statuses, latest } = setup();

    expect(sockets[0].url).toBe("ws://fleet.test/api/sessions/s%201/terminals/t1/socket?cols=100&rows=30");
    expect(sockets[0].binaryType).toBe("arraybuffer");
    latest().open();
    latest().output("old output\r\n");
    latest().control({ type: "ready" });
    latest().output("live");

    expect(events).toEqual(["reset", "out:old output\r\n", "ready", "out:live"]);
    expect(statuses).toEqual(["connecting", "open"]);
  });

  it("sends input as binary and resize and clear as text", () => {
    const { connection, latest, sentText } = setup();
    latest().open();
    latest().control({ type: "ready" });

    connection.write("ls\r");
    connection.resize(90, 20);
    connection.clear();

    // Bytes, not text (jsdom's Uint8Array is another realm's, so no instanceof).
    expect(typeof latest().sent[0]).not.toBe("string");
    expect(sentText(latest())).toEqual(["ls\r", '{"type":"resize","cols":90,"rows":20}', '{"type":"clear"}']);
  });

  it("keeps what's typed before the socket is ready and sends it after", () => {
    const { connection, latest, sentText } = setup();

    connection.write("echo early\r");
    latest().open();
    expect(latest().sent).toEqual([]);
    latest().control({ type: "ready" });

    expect(sentText(latest())).toEqual(["echo early\r"]);
  });

  it("reports the exit and doesn't reconnect after the shell ends", () => {
    const { events, statuses, scheduled, latest } = setup();
    latest().open();
    latest().control({ type: "ready" });

    latest().control({ type: "exit", exitCode: 3 });
    latest().drop(1000);

    expect(events).toContain("exit:3");
    expect(statuses.at(-1)).toBe("ended");
    expect(scheduled).toEqual([]);
  });

  it("reports a null exit code when Fleet ended the shell", () => {
    const { events, latest } = setup();
    latest().open();
    latest().control({ type: "exit", exitCode: null });

    expect(events).toContain("exit:null");
  });

  it("reconnects after a drop, and the new connection starts from a reset", () => {
    const { sockets, events, scheduled, latest } = setup();
    latest().open();
    latest().control({ type: "ready" });

    latest().drop(4001);
    expect(scheduled[0].ms).toBe(RECONNECT_DELAYS_MS[0]);
    scheduled[0].callback();
    latest().open();
    latest().output("replayed");
    latest().control({ type: "ready" });

    expect(sockets).toHaveLength(2);
    expect(events.slice(-3)).toEqual(["reset", "out:replayed", "ready"]);
  });

  it("gives up after repeated attempts that never get the scrollback", () => {
    const { sockets, statuses, scheduled, latest } = setup();

    for (let i = 0; i < MAX_FAILED_ATTEMPTS; i++) {
      const before = scheduled.length;
      latest().drop(1006);
      if (scheduled.length > before) scheduled[scheduled.length - 1].callback();
    }

    expect(statuses.at(-1)).toBe("failed");
    expect(sockets).toHaveLength(MAX_FAILED_ATTEMPTS);
  });

  it("closes normally when disposed and doesn't reconnect", () => {
    const { connection, scheduled, latest } = setup();
    latest().open();
    const socket = latest();

    connection.dispose();
    socket.drop(1000);

    expect(socket.closedWith).toBe(1000);
    expect(scheduled).toEqual([]);
  });

  it("passes on a clear from another window", () => {
    const { events, latest } = setup();
    latest().open();
    latest().control({ type: "ready" });

    latest().control({ type: "cleared" });

    expect(events.at(-1)).toBe("cleared");
  });
});
