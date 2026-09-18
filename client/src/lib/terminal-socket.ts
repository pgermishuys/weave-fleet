import { terminalSocketUrl } from "@/lib/terminal-api";

/**
 * What a terminal view hears from its connection. Each (re)connect starts with
 * `onReset`, then the saved scrollback arrives through `onOutput`, then
 * `onReady`, then live output.
 */
export interface TerminalConnectionHandlers {
  onReset(): void;
  onOutput(data: Uint8Array): void;
  onReady(): void;
  onCleared(): void;
  /** The shell ended. `exitCode` is null when Fleet ended it (tab closed, session archived). */
  onExit(exitCode: number | null): void;
  onStatus(status: TerminalConnectionStatus): void;
}

export type TerminalConnectionStatus = "connecting" | "open" | "reconnecting" | "ended" | "failed";

export interface TerminalConnection {
  write(data: string | Uint8Array): void;
  resize(cols: number, rows: number): void;
  clear(): void;
  dispose(): void;
}

export interface ConnectTerminalOptions {
  sessionId: string;
  terminalId: string;
  /** Where the terminal lives when it isn't a session's, e.g. the setup terminal. */
  basePath?: string;
  cols: number;
  rows: number;
  handlers: TerminalConnectionHandlers;
  /** Stand-in for `new WebSocket(url)`, for tests. */
  createSocket?: (url: string) => WebSocket;
  /** Stand-in for `setTimeout`, for tests. */
  schedule?: (callback: () => void, ms: number) => unknown;
}

/** Waits between reconnect attempts. */
export const RECONNECT_DELAYS_MS = [250, 500, 1000, 2000, 4000, 8000];

/** Attempts in a row that never got the scrollback before giving up (the terminal is gone, or the server refuses). */
export const MAX_FAILED_ATTEMPTS = 5;

/** Input typed while the socket is reconnecting is kept, up to this many bytes. */
const MAX_PENDING_INPUT = 4096;

/** Server close codes: 1000 = the shell ended or the tab was closed elsewhere. Anything else is worth a reconnect. */
const NORMAL_CLOSURE = 1000;

const encoder = new TextEncoder();

/**
 * Opens a terminal's WebSocket and keeps it open: it reconnects after a drop,
 * and the server replays the scrollback each time, so the view resets first.
 * It stops after the shell ends, the tab closes elsewhere, or
 * {@link MAX_FAILED_ATTEMPTS} attempts in a row fail before any scrollback.
 */
export function connectTerminal(options: ConnectTerminalOptions): TerminalConnection {
  const { sessionId, terminalId, handlers } = options;
  const createSocket = options.createSocket ?? ((url: string) => new WebSocket(url));
  const schedule = options.schedule ?? ((callback: () => void, ms: number) => setTimeout(callback, ms));

  let cols = options.cols;
  let rows = options.rows;
  let socket: WebSocket | null = null;
  let disposed = false;
  let ended = false;
  let failedAttempts = 0;
  let attempt = 0;
  let pending: Uint8Array[] = [];
  let pendingBytes = 0;

  function open(): void {
    if (disposed || ended) return;
    handlers.onStatus(attempt === 0 ? "connecting" : "reconnecting");
    attempt += 1;

    const current = createSocket(terminalSocketUrl(sessionId, terminalId, cols, rows, options.basePath));
    current.binaryType = "arraybuffer";
    socket = current;
    let reachedReady = false;

    current.onopen = () => {
      if (socket !== current) return;
      handlers.onReset();
    };

    current.onmessage = (event: MessageEvent) => {
      if (socket !== current) return;
      if (typeof event.data !== "string") {
        handlers.onOutput(new Uint8Array(event.data as ArrayBuffer));
        return;
      }

      const message = parseControl(event.data);
      switch (message?.type) {
        case "ready":
          reachedReady = true;
          failedAttempts = 0;
          handlers.onStatus("open");
          handlers.onReady();
          flushPending(current);
          break;
        case "cleared":
          handlers.onCleared();
          break;
        case "exit":
          ended = true;
          handlers.onExit(typeof message.exitCode === "number" ? message.exitCode : null);
          break;
      }
    };

    current.onclose = (event: CloseEvent) => {
      if (socket !== current) return;
      socket = null;
      if (disposed) return;

      if (ended || event.code === NORMAL_CLOSURE) {
        ended = true;
        handlers.onStatus("ended");
        return;
      }

      if (!reachedReady) failedAttempts += 1;
      if (failedAttempts >= MAX_FAILED_ATTEMPTS) {
        handlers.onStatus("failed");
        return;
      }

      const delay = RECONNECT_DELAYS_MS[Math.min(attempt - 1, RECONNECT_DELAYS_MS.length - 1)];
      handlers.onStatus("reconnecting");
      schedule(open, delay);
    };
  }

  function flushPending(target: WebSocket): void {
    for (const chunk of pending) target.send(chunk);
    pending = [];
    pendingBytes = 0;
  }

  function isOpen(target: WebSocket | null): target is WebSocket {
    return target !== null && target.readyState === WebSocket.OPEN;
  }

  open();

  return {
    write(data) {
      if (disposed || ended) return;
      const bytes = typeof data === "string" ? encoder.encode(data) : data;
      if (isOpen(socket)) {
        socket.send(bytes);
        return;
      }
      if (pendingBytes + bytes.length > MAX_PENDING_INPUT) return;
      pending.push(bytes);
      pendingBytes += bytes.length;
    },
    resize(nextCols, nextRows) {
      cols = nextCols;
      rows = nextRows;
      if (isOpen(socket)) socket.send(JSON.stringify({ type: "resize", cols: nextCols, rows: nextRows }));
    },
    clear() {
      if (isOpen(socket)) socket.send(JSON.stringify({ type: "clear" }));
    },
    dispose() {
      if (disposed) return;
      disposed = true;
      const current = socket;
      socket = null;
      current?.close(NORMAL_CLOSURE, "Closed by the page.");
    },
  };
}

function parseControl(text: string): { type?: unknown; exitCode?: unknown } | null {
  try {
    const value: unknown = JSON.parse(text);
    return value && typeof value === "object" ? (value as { type?: unknown; exitCode?: unknown }) : null;
  } catch {
    return null;
  }
}
