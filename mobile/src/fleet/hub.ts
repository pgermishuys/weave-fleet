// One SignalR connection to the paired Fleet: the same hub, methods and wire shape as the web client
// (client/src/composables/use-signalr-socket.ts), with the device key as the bearer.
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
import { AppState } from "react-native";
import type { DomainEvent } from "@fleet/lib/domain-events";
import type { SessionSnapshot } from "@fleet/lib/session-snapshot";
import { currentCredentials } from "~/fleet/credentials";

type SnapshotListener = (snapshot: SessionSnapshot) => void;
type EventListener = (event: DomainEvent) => void;
export type ConnectionState = "connecting" | "connected" | "reconnecting" | "offline";

/** Maps a hub event to a DomainEvent: the wire calls the payload "properties" (same as the web client's toDomainEvent). */
function toDomainEvent(eventId: number | null, data: unknown): DomainEvent {
  const wire = data as { type: string; properties?: unknown };
  return { type: wire.type, payload: wire.properties, ...(eventId !== null ? { eventId } : {}) } as DomainEvent;
}

class FleetHub {
  private connection: HubConnection | null = null;
  private starting: Promise<void> | null = null;
  private sessions = new Map<string, { snapshot: Set<SnapshotListener>; events: Set<EventListener> }>();
  private global = new Set<EventListener>();
  private stateListeners = new Set<(state: ConnectionState) => void>();
  state: ConnectionState = "offline";

  constructor() {
    // iOS and Android drop sockets in the background; come back as soon as the app does.
    AppState.addEventListener("change", (next) => {
      if (next === "active" && this.connection?.state === HubConnectionState.Disconnected) void this.restart();
    });
  }

  private setState(state: ConnectionState) {
    this.state = state;
    for (const listener of this.stateListeners) listener(state);
  }

  onState(listener: (state: ConnectionState) => void): () => void {
    this.stateListeners.add(listener);
    return () => this.stateListeners.delete(listener);
  }

  private async ensure(): Promise<void> {
    if (this.connection?.state === HubConnectionState.Connected) return;
    if (this.starting) return this.starting;
    this.starting = this.start().finally(() => (this.starting = null));
    return this.starting;
  }

  private async start(): Promise<void> {
    const credentials = currentCredentials();
    if (!credentials) throw new Error("not paired");
    const connection = new HubConnectionBuilder()
      .withUrl(`${credentials.baseUrl}/hubs/session-events`, { accessTokenFactory: () => credentials.token, withCredentials: false })
      .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
      .configureLogging(LogLevel.Warning)
      .build();
    connection.on("Event", (topic: string, eventId: number | null, data: unknown) => this.dispatch(topic, eventId, data));
    connection.onreconnecting(() => this.setState("reconnecting"));
    connection.onreconnected(() => {
      this.setState("connected");
      void this.resubscribe();
    });
    connection.onclose(() => this.setState("offline"));
    this.connection = connection;
    this.setState("connecting");
    await connection.start();
    this.setState("connected");
    await this.resubscribe();
  }

  private async restart(): Promise<void> {
    this.connection = null;
    await this.ensure().catch(() => this.setState("offline"));
  }

  private async resubscribe(): Promise<void> {
    const connection = this.connection;
    if (!connection) return;
    if (this.global.size > 0) await connection.invoke("SubscribeToSessionsTopicAsync").catch(() => {});
    for (const sessionId of this.sessions.keys()) await this.snapshot(sessionId);
  }

  private async snapshot(sessionId: string): Promise<void> {
    const snapshot = await this.connection?.invoke<SessionSnapshot>("SubscribeToSessionAsync", sessionId);
    const listeners = this.sessions.get(sessionId);
    if (snapshot && listeners) for (const listener of listeners.snapshot) listener(snapshot);
  }

  private dispatch(topic: string, eventId: number | null, data: unknown) {
    const event = toDomainEvent(eventId, data);
    if (topic === "sessions") {
      for (const listener of this.global) listener(event);
      return;
    }
    const sessionId = topic.startsWith("session:") ? topic.slice(8) : topic;
    const listeners = this.sessions.get(sessionId);
    if (listeners) for (const listener of listeners.events) listener(event);
  }

  /** Follows one session: a snapshot now (and after every reconnect), then its live events. */
  subscribeSession(sessionId: string, onSnapshot: SnapshotListener, onEvent: EventListener): () => void {
    let entry = this.sessions.get(sessionId);
    if (!entry) this.sessions.set(sessionId, (entry = { snapshot: new Set(), events: new Set() }));
    entry.snapshot.add(onSnapshot);
    entry.events.add(onEvent);
    void this.ensure().then(() => this.snapshot(sessionId)).catch(() => this.setState("offline"));
    return () => {
      entry!.snapshot.delete(onSnapshot);
      entry!.events.delete(onEvent);
      if (entry!.snapshot.size === 0) {
        this.sessions.delete(sessionId);
        void this.connection?.invoke("UnsubscribeFromSessionAsync", sessionId).catch(() => {});
      }
    };
  }

  /** Follows the global "sessions" topic: activity changes and notifications for every session. */
  subscribeSessions(onEvent: EventListener): () => void {
    const first = this.global.size === 0;
    this.global.add(onEvent);
    if (first) void this.ensure().then(() => this.connection?.invoke("SubscribeToSessionsTopicAsync")).catch(() => this.setState("offline"));
    return () => this.global.delete(onEvent);
  }

  /** A session's snapshot once, without following it. */
  async peek(sessionId: string): Promise<SessionSnapshot> {
    await this.ensure();
    const snapshot = await this.connection!.invoke<SessionSnapshot>("SubscribeToSessionAsync", sessionId);
    if (!this.sessions.has(sessionId)) void this.connection?.invoke("UnsubscribeFromSessionAsync", sessionId).catch(() => {});
    return snapshot;
  }

  async stop(): Promise<void> {
    await this.connection?.stop();
    this.connection = null;
  }
}

export const hub = new FleetHub();
