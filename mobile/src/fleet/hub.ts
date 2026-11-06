// One SignalR connection to one machine: the same hub, methods and wire shape as the web client
// (client/src/composables/use-signalr-socket.ts), with the device key as the bearer.
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
import type { DomainEvent } from "@fleet/lib/domain-events";
import type { SessionSnapshot } from "@fleet/lib/session-snapshot";
import type { Machine } from "~/fleet/machines-core";
import { toDomainEvent } from "~/fleet/session-list";

export type ConnectionState = "connecting" | "connected" | "reconnecting" | "offline" | "signed-out";

type SnapshotListener = (snapshot: SessionSnapshot) => void;
type EventListener = (event: DomainEvent) => void;
const RETRY_MS = [1000, 2000, 5000, 10000];

export class MachineHub {
  state: ConnectionState = "offline";
  private connection: HubConnection | null = null;
  private wanted = false;
  private attempt = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private sessions = new Map<string, { snapshot: Set<SnapshotListener>; events: Set<EventListener> }>();
  private global = new Set<EventListener>();

  private opening = false;
  private moving = false;

  /**
   * `onState` hears every change; `onLive` hears each time the connection is (re)established, when the list should be
   * read again. `beforeOpen` runs before each connect, and may change `machine.baseUrl` (a machine with several
   * addresses picks the one that answers).
   */
  constructor(
    private machine: Pick<Machine, "baseUrl" | "token">,
    private onState: (state: ConnectionState) => void,
    private onLive: () => void,
    private beforeOpen: () => Promise<void> = async () => {},
  ) {}

  private set(state: ConnectionState) {
    if (this.state === state) return;
    this.state = state;
    this.onState(state);
  }

  /** Connects, and stays connected (retrying) until `stop`. */
  start(): void {
    this.wanted = true;
    if (this.connection || this.timer || this.opening || this.state === "signed-out") return;
    void this.open();
  }

  /** Closes the socket and keeps the subscriptions, ready for `start`. iOS suspends sockets in the background anyway. */
  async stop(): Promise<void> {
    this.wanted = false;
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    const connection = this.connection;
    this.connection = null;
    if (this.state !== "signed-out") this.set("offline");
    await connection?.stop().catch(() => {});
  }

  markSignedOut() {
    this.set("signed-out");
    void this.stop().then(() => this.set("signed-out"));
  }

  /** SignalR's connection to the hub at `url`, with the device key as the bearer. Tests replace it. */
  protected connectTo(url: string): HubConnection {
    return new HubConnectionBuilder()
      .withUrl(`${url}/hubs/session-events`, { accessTokenFactory: () => this.machine.token, withCredentials: false })
      .withAutomaticReconnect(RETRY_MS)
      .configureLogging(LogLevel.None)
      .build();
  }

  /** A connection to `url` whose events count only while it's the one in use. */
  private build(url: string): HubConnection {
    const connection = this.connectTo(url);
    const current = () => this.connection === connection;
    connection.on("Event", (topic: string, eventId: number | null, data: unknown) => current() && this.dispatch(topic, eventId, data));
    connection.onreconnecting(() => current() && this.set("reconnecting"));
    connection.onreconnected(() => current() && void this.live(connection));
    connection.onclose(() => {
      if (!current()) return;
      this.connection = null;
      this.set("offline");
      this.retry();
    });
    return connection;
  }

  private async open(): Promise<void> {
    this.opening = true;
    this.set(this.attempt === 0 ? "connecting" : "reconnecting");
    try {
      await this.beforeOpen();
    } catch {
      // Picking an address never stops a connect: the one in use is tried as before.
    } finally {
      this.opening = false;
    }
    if (!this.wanted || this.connection || this.state === "signed-out") return;
    const connection = this.build(this.machine.baseUrl);
    this.connection = connection;
    try {
      await connection.start();
      if (this.connection !== connection) return await connection.stop();
      this.attempt = 0;
      await this.live(connection);
    } catch (error) {
      if (this.connection !== connection) return;
      this.connection = null;
      if (/\b401\b|unauthori[sz]ed/i.test(String(error))) this.set("signed-out");
      else {
        this.set("offline");
        this.retry();
      }
    }
  }

  /**
   * Moves to a better address: a connection there is opened and subscribed first, and only then becomes the one in
   * use, with fresh snapshots for every session followed through it, before the old one closes. One that answers but
   * won't hold a connection leaves the working one alone (T3 Code's #24516 dropped a healthy connection for it).
   * True when it moved.
   */
  async moveTo(url: string): Promise<boolean> {
    const old = this.connection;
    if (!old || this.moving || this.state !== "connected") return false;
    this.moving = true;
    let next: HubConnection | null = this.build(url);
    try {
      await next.start();
      await next.invoke("SubscribeToSessionsTopicAsync");
      if (this.connection !== old || this.state !== "connected" || !this.wanted) return false;
      this.connection = next;
      next = null;
      await this.live(this.connection);
      await old.stop().catch(() => {});
      return true;
    } catch {
      return false;
    } finally {
      this.moving = false;
      await next?.stop().catch(() => {});
    }
  }

  private retry() {
    if (!this.wanted) return;
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.open();
    }, RETRY_MS[Math.min(this.attempt++, RETRY_MS.length - 1)]);
  }

  /** Connected (again): resubscribe every topic, then tell the owner to re-read. */
  private async live(connection: HubConnection) {
    this.set("connected");
    await connection.invoke("SubscribeToSessionsTopicAsync").catch(() => {});
    for (const sessionId of this.sessions.keys()) await this.snapshot(sessionId);
    this.onLive();
  }

  private async snapshot(sessionId: string): Promise<void> {
    if (this.connection?.state !== HubConnectionState.Connected) return;
    const snapshot = await this.connection.invoke<SessionSnapshot>("SubscribeToSessionAsync", sessionId).catch(() => null);
    const entry = this.sessions.get(sessionId);
    if (snapshot && entry) for (const listener of entry.snapshot) listener(snapshot);
  }

  private dispatch(topic: string, eventId: number | null, data: unknown) {
    const event = toDomainEvent(eventId, data);
    if (topic === "sessions") {
      for (const listener of this.global) listener(event);
      return;
    }
    const entry = this.sessions.get(topic.startsWith("session:") ? topic.slice(8) : topic);
    if (entry) for (const listener of entry.events) listener(event);
  }

  /** Follows one session: a snapshot now (and after every reconnect), then its live events. */
  subscribeSession(sessionId: string, onSnapshot: SnapshotListener, onEvent: EventListener): () => void {
    let entry = this.sessions.get(sessionId);
    if (!entry) this.sessions.set(sessionId, (entry = { snapshot: new Set(), events: new Set() }));
    entry.snapshot.add(onSnapshot);
    entry.events.add(onEvent);
    void this.snapshot(sessionId);
    return () => {
      entry.snapshot.delete(onSnapshot);
      entry.events.delete(onEvent);
      if (entry.snapshot.size === 0) {
        this.sessions.delete(sessionId);
        void this.connection?.invoke("UnsubscribeFromSessionAsync", sessionId).catch(() => {});
      }
    };
  }

  /** The `sessions` topic: activity changes and created/archived notices for every session on the machine. */
  subscribeSessions(onEvent: EventListener): () => void {
    this.global.add(onEvent);
    return () => this.global.delete(onEvent);
  }
}
