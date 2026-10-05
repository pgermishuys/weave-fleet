/**
 * A live view of one machine for the phone's inbox: its sessions, and what the waiting ones wait on. Listens on the
 * machine's event hub (the global `sessions` topic) and re-reads the list when something changes; if the hub won't
 * connect it polls every 15 seconds; if the machine doesn't answer it keeps the last list and says when it last heard.
 *
 * Home is reached same-origin with the phone's cookie; any other machine cross-origin with the phone's own token for
 * it (a device grant), never with cookies. No Vue, so a native wrapper can reuse it.
 */
import { HubConnectionBuilder, HubConnectionState, type HubConnection } from "@microsoft/signalr";
import type { SessionListItem } from "@/api/client";
import type { PermissionAsk } from "@/composables/use-session-permissions";
import { toPermissionAsk } from "@/composables/use-session-permissions";
import { convertFleetMessageToAccumulated, type FleetMessage } from "@/lib/pagination-utils";
import type { FeedStatus, InboxAsk } from "@/lib/phone/inbox";
import { getQuestionInput } from "@/lib/question-types";
import type { AccumulatedToolPart } from "@/lib/client-types";

export const POLL_INTERVAL_MS = 15_000;
/** Even with a live hub, re-read now and then: a missed event shouldn't leave the inbox wrong for long. */
export const SAFETY_REFRESH_MS = 60_000;
const SESSION_PAGE = 100;

/** Where a machine is and how to present the phone's key to it. */
export interface FeedTarget {
  machineId: string;
  /** Base URL; empty for home (same origin). */
  baseUrl: string;
  /** The phone's token for this machine; null for home, where the cookie does. */
  token: string | null;
}

export interface FeedSnapshot {
  status: FeedStatus;
  lastHeardAt: number | null;
  sessions: SessionListItem[];
  asks: Record<string, InboxAsk>;
  error: string | null;
}

/** The parts of a SignalR connection the feed uses, so tests can stand one in. */
export interface FeedHub {
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke(method: string, ...args: unknown[]): Promise<unknown>;
  on(method: string, handler: (...args: unknown[]) => void): void;
  onclose(handler: (error?: Error) => void): void;
  readonly state: HubConnectionState | string;
}

export interface FeedOptions {
  target: FeedTarget;
  onChange: (snapshot: FeedSnapshot) => void;
  /** Called once when the machine turns the phone's token away; returns a new token, or null. */
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

export class MachineFeed {
  private readonly options: FeedOptions;
  private token: string | null;
  private hub: FeedHub | null = null;
  private pollTimer: ReturnType<typeof setInterval> | null = null;
  private safetyTimer: ReturnType<typeof setInterval> | null = null;
  private refreshTimer: ReturnType<typeof setTimeout> | null = null;
  private refreshing: Promise<void> | null = null;
  private renewed = false;
  private stopped = false;
  private snapshot: FeedSnapshot = { status: "connecting", lastHeardAt: null, sessions: [], asks: {}, error: null };

  constructor(options: FeedOptions) {
    this.options = options;
    this.token = options.target.token;
  }

  get state(): FeedSnapshot {
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
      void this.refresh();
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
      this.hub = hub;
      this.stopPolling();
      if (this.snapshot.status !== "unreachable") this.update({ status: "live" });
    } catch {
      this.startPolling();
    }
  }

  private startPolling(): void {
    if (this.pollTimer || this.stopped) return;
    if (this.snapshot.status !== "unreachable") this.update({ status: "polling" });
    this.pollTimer = setInterval(() => void this.refresh(), POLL_INTERVAL_MS);
  }

  private stopPolling(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
    this.pollTimer = null;
  }

  private request(path: string): Promise<Response> {
    const { baseUrl } = this.options.target;
    const headers: Record<string, string> = {};
    if (this.token) headers.Authorization = `Bearer ${this.token}`;
    return this.fetcher(`${baseUrl}${path}`, { headers, credentials: baseUrl ? "omit" : "include" });
  }

  /** Reads the session list and the asks of waiting sessions. Concurrent calls share one read. */
  refresh(): Promise<void> {
    this.refreshing ??= this.read().finally(() => {
      this.refreshing = null;
    });
    return this.refreshing;
  }

  private async read(): Promise<void> {
    let response: Response;
    try {
      response = await this.request(`/api/sessions?limit=${SESSION_PAGE}&offset=0`);
    } catch {
      this.update({ status: "unreachable", error: null });
      return;
    }

    if (response.status === 401 && !this.renewed && this.options.onUnauthorized) {
      this.renewed = true;
      const next = await this.options.onUnauthorized();
      if (next) {
        this.token = next;
        return this.read();
      }
    }
    if (!response.ok) {
      this.update({
        status: "unreachable",
        error: response.status === 401 ? "This phone's key for this machine stopped working." : `It answered ${response.status}.`,
      });
      return;
    }

    const sessions = await response.json() as SessionListItem[];
    const asks = await this.readAsks(sessions.filter((session) => session.sessionStatus === "waiting_input"));
    const live = this.hub?.state === HubConnectionState.Connected;
    this.update({
      sessions,
      asks,
      lastHeardAt: this.now(),
      status: live ? "live" : this.pollTimer ? "polling" : this.snapshot.status === "unreachable" ? "polling" : this.snapshot.status,
      error: null,
    });
  }

  private async readAsks(waiting: readonly SessionListItem[]): Promise<Record<string, InboxAsk>> {
    const asks: Record<string, InboxAsk> = {};
    await Promise.all(waiting.slice(0, 12).map(async (session) => {
      const ask = await this.readAsk(session.session.id).catch(() => null);
      if (ask) asks[session.session.id] = ask;
    }));
    return asks;
  }

  private async readAsk(sessionId: string): Promise<InboxAsk | null> {
    const permissions = await this.request(`/api/sessions/${encodeURIComponent(sessionId)}/permissions`);
    if (permissions.ok) {
      const list = (await permissions.json() as unknown[]).map(toPermissionAsk).filter((ask): ask is PermissionAsk => ask !== null);
      if (list.length) return { kind: "permission", ask: list[0] };
    }

    // No permission ask: look for a question the agent is waiting on in the last few messages.
    const messages = await this.request(`/api/sessions/${encodeURIComponent(sessionId)}/messages?limit=6`);
    if (!messages.ok) return null;
    const body = await messages.json() as { messages?: FleetMessage[] };
    return findPendingQuestion(body.messages ?? []);
  }

  private update(patch: Partial<FeedSnapshot>): void {
    this.snapshot = { ...this.snapshot, ...patch };
    this.options.onChange(this.snapshot);
  }
}

/** The newest question tool call still waiting for an answer, from Fleet's message shape. */
export function findPendingQuestion(messages: readonly FleetMessage[]): InboxAsk | null {
  for (const message of [...messages].reverse()) {
    const accumulated = convertFleetMessageToAccumulated(message);
    for (const part of [...accumulated.parts].reverse()) {
      if (part.type !== "tool") continue;
      const tool = part as AccumulatedToolPart;
      const status = (tool.state as { status?: string } | null)?.status;
      if (status !== "pending" && status !== "running") continue;
      const input = getQuestionInput(tool);
      if (input?.questions.length) {
        return { kind: "question", requestId: tool.callId, question: input.questions[0], more: input.questions.length - 1 };
      }
    }
  }
  return null;
}
