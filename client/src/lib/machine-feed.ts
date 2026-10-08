/**
 * A live view of one machine: listens on its event hub (the global `sessions` topic) and re-reads when something
 * changes; if the hub won't connect it polls every 15 seconds; if the machine doesn't answer it keeps the last read and
 * says when it last heard. What a read fetches is the caller's: the phone's inbox reads sessions and their asks, the
 * sidebar sessions and projects.
 *
 * Home is reached same-origin with the cookie; any other machine cross-origin with a token for it, never with cookies.
 * No Vue, so a native wrapper can reuse it.
 */
import { HubConnectionBuilder, HubConnectionState, type HubConnection } from "@microsoft/signalr";

export const POLL_INTERVAL_MS = 15_000;
/** Even with a live hub, re-read now and then: a missed event shouldn't leave the view wrong for long. */
export const SAFETY_REFRESH_MS = 60_000;
/** Feeds close after the page has been off screen this long, and open again when it comes back. */
export const HIDDEN_CLOSE_MS = 5 * 60_000;

/** How a machine's feed is getting news: live over its event hub, by polling, or not at all. */
export type FeedStatus = "connecting" | "live" | "polling" | "unreachable";

/** Where a machine is and how to present a key to it. */
export interface FeedTarget {
  machineId: string;
  /** Base URL; empty for home (same origin). */
  baseUrl: string;
  /** The token for this machine; null for home, where the cookie does. */
  token: string | null;
}

/** How the feed is doing, plus what its last read returned. */
export type FeedSnapshot<T> = T & {
  status: FeedStatus;
  lastHeardAt: number | null;
  error: string | null;
};

/** A request to the feed's machine, with its key. */
export type FeedRequest = (path: string) => Promise<Response>;

/** Thrown by a read to mark the machine unreachable with a reason the user can read. */
export class FeedError extends Error {}

/** The parts of a SignalR connection the feed uses, so tests can stand one in. */
export interface FeedHub {
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke(method: string, ...args: unknown[]): Promise<unknown>;
  on(method: string, handler: (...args: unknown[]) => void): void;
  onclose(handler: (error?: Error) => void): void;
  readonly state: HubConnectionState | string;
}

export interface MachineFeedOptions<T> {
  target: FeedTarget;
  /** What the feed holds before its first read. */
  initial: T;
  /** One read of the machine. Throwing marks it unreachable, with the message of a {@link FeedError}. */
  read: (request: FeedRequest) => Promise<T>;
  onChange: (snapshot: FeedSnapshot<T>) => void;
  /** Called once when the machine turns the token away; returns a new token, or null. */
  onUnauthorized?: () => Promise<string | null>;
  createHub?: (url: string, token: string | null) => FeedHub;
  fetcher?: typeof fetch;
  now?: () => number;
}

function defaultHub(url: string, token: string | null): FeedHub {
  const connection: HubConnection = new HubConnectionBuilder()
    .withUrl(url, token ? { accessTokenFactory: () => token, withCredentials: false } : {})
    .withAutomaticReconnect([2000, 5000, 15000, 30000])
    .build();
  return {
    start: () => connection.start(),
    stop: () => connection.stop(),
    invoke: (method, ...args) => connection.invoke(method, ...args),
    on: (method, handler) => connection.on(method, handler),
    onclose: (handler) => connection.onclose(handler),
    get state() {
      return connection.state;
    },
  };
}

export class MachineFeed<T extends object> {
  private readonly options: MachineFeedOptions<T>;
  private token: string | null;
  private hub: FeedHub | null = null;
  private pollTimer: ReturnType<typeof setInterval> | null = null;
  private safetyTimer: ReturnType<typeof setInterval> | null = null;
  private refreshTimer: ReturnType<typeof setTimeout> | null = null;
  private refreshing: Promise<void> | null = null;
  /** An event arrived while a read was under way: read again once it's done, so the change isn't missed. */
  private readAgain = false;
  private renewed = false;
  private stopped = false;
  private snapshot: FeedSnapshot<T>;

  constructor(options: MachineFeedOptions<T>) {
    this.options = options;
    this.token = options.target.token;
    this.snapshot = { ...options.initial, status: "connecting", lastHeardAt: null, error: null };
  }

  get state(): FeedSnapshot<T> {
    return this.snapshot;
  }

  /** Bound: the browser refuses `fetch` called as a method of anything but `window`. */
  private get fetcher(): typeof fetch {
    return this.options.fetcher ?? globalThis.fetch.bind(globalThis);
  }

  private now(): number {
    return this.options.now?.() ?? Date.now();
  }

  async start(): Promise<void> {
    this.stopped = false;
    await this.refresh();
    if (this.stopped) return;
    await this.connect();
    if (this.stopped) return;
    this.safetyTimer = setInterval(() => void this.refresh(), SAFETY_REFRESH_MS);
  }

  async stop(): Promise<void> {
    this.stopped = true;
    if (this.pollTimer) clearInterval(this.pollTimer);
    if (this.safetyTimer) clearInterval(this.safetyTimer);
    if (this.refreshTimer) clearTimeout(this.refreshTimer);
    this.pollTimer = this.safetyTimer = this.refreshTimer = null;
    const hub = this.hub;
    this.hub = null;
    if (hub) await hub.stop().catch(() => undefined);
  }

  /** Re-reads soon, folding a burst of events into one read. */
  scheduleRefresh(delayMs = 300): void {
    if (this.refreshTimer || this.stopped) return;
    this.refreshTimer = setTimeout(() => {
      this.refreshTimer = null;
      if (this.refreshing) this.readAgain = true;
      else void this.refresh();
    }, delayMs);
  }

  private async connect(): Promise<void> {
    const url = `${this.options.target.baseUrl}/hubs/session-events`;
    const hub = (this.options.createHub ?? defaultHub)(url, this.token);
    hub.on("Event", (topic) => {
      if (topic === "sessions") this.scheduleRefresh();
    });
    hub.onclose(() => {
      if (!this.stopped) this.startPolling();
    });
    try {
      await hub.start();
      await hub.invoke("SubscribeToSessionsTopicAsync");
      // Stopped while it connected: nothing will stop this connection later.
      if (this.stopped) {
        await hub.stop().catch(() => undefined);
        return;
      }
      this.hub = hub;
      this.stopPolling();
      if (this.snapshot.status !== "unreachable") this.update({ status: "live" } as Partial<FeedSnapshot<T>>);
    } catch {
      this.startPolling();
    }
  }

  private startPolling(): void {
    if (this.pollTimer || this.stopped) return;
    if (this.snapshot.status !== "unreachable") this.update({ status: "polling" } as Partial<FeedSnapshot<T>>);
    this.pollTimer = setInterval(() => void this.refresh(), POLL_INTERVAL_MS);
  }

  private stopPolling(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
    this.pollTimer = null;
  }

  /** A request with the key; when the machine turns the key away, asks for a new one once and tries again. */
  private async request(path: string): Promise<Response> {
    const response = await this.send(path);
    if (response.status !== 401 || this.renewed || !this.options.onUnauthorized) return response;
    this.renewed = true;
    const next = await this.options.onUnauthorized();
    if (!next) return response;
    this.token = next;
    return this.send(path);
  }

  private send(path: string): Promise<Response> {
    const { baseUrl } = this.options.target;
    const headers: Record<string, string> = {};
    if (this.token) headers.Authorization = `Bearer ${this.token}`;
    return this.fetcher(`${baseUrl}${path}`, { headers, credentials: baseUrl ? "omit" : "include" });
  }

  /** Reads the machine. Concurrent calls share one read. */
  refresh(): Promise<void> {
    this.refreshing ??= this.read().finally(() => {
      this.refreshing = null;
      if (this.readAgain && !this.stopped) {
        this.readAgain = false;
        void this.refresh();
      }
    });
    return this.refreshing;
  }

  private async read(): Promise<void> {
    let data: T;
    try {
      data = await this.options.read((path) => this.request(path));
    } catch (error) {
      this.update({ status: "unreachable", error: error instanceof FeedError ? error.message : null } as Partial<FeedSnapshot<T>>);
      return;
    }

    const live = this.hub?.state === HubConnectionState.Connected;
    this.update({
      ...data,
      lastHeardAt: this.now(),
      status: live ? "live" : this.pollTimer ? "polling" : this.snapshot.status === "unreachable" ? "polling" : this.snapshot.status,
      error: null,
    });
  }

  private update(patch: Partial<FeedSnapshot<T>>): void {
    this.snapshot = { ...this.snapshot, ...patch };
    this.options.onChange(this.snapshot);
  }
}
