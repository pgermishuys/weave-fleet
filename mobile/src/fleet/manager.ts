// Every paired machine's connection and session list, in one place. No React Native in here: the app tells it when it
// goes to the background (`setForeground`), and hooks.ts reads it.
import { applySessionsEvent, sectionSessions, type SessionSection } from "~/fleet/session-list";
import { machineApi, type MachineApi, type SessionListItem } from "~/fleet/api";
import { MachineHub, type ConnectionState } from "~/fleet/hub";
import type { Machine, MachinesStore } from "~/fleet/machines-core";

export interface MachineView {
  machineId: string;
  machineName: string;
  baseUrl: string;
  state: ConnectionState;
}

export interface FleetSnapshot {
  machines: MachineView[];
  sections: SessionSection[];
  /** True until each machine has answered once. */
  loading: boolean;
}

interface Conn {
  machine: Machine;
  hub: MachineHub;
  api: MachineApi;
  items: SessionListItem[];
  loaded: boolean;
  fetchTimer: ReturnType<typeof setTimeout> | null;
}

export class FleetManager {
  private conns = new Map<string, Conn>();
  private listeners = new Set<() => void>();
  private foreground = true;
  private cached: FleetSnapshot = { machines: [], sections: [], loading: false };

  constructor(private store: MachinesStore) {
    store.subscribe(() => this.sync());
    this.sync();
  }

  /** Make the connections match the paired machines. */
  private sync() {
    const paired = this.store.all();
    for (const [id, conn] of this.conns) {
      const now = paired.find((m) => m.machineId === id);
      if (now && now.token === conn.machine.token && now.baseUrl === conn.machine.baseUrl) continue;
      this.drop(conn);
      this.conns.delete(id);
    }
    for (const machine of paired) if (!this.conns.has(machine.machineId)) this.conns.set(machine.machineId, this.connect(machine));
    this.publish();
  }

  private connect(machine: Machine): Conn {
    const conn: Conn = {
      machine,
      items: [],
      loaded: false,
      fetchTimer: null,
      api: machineApi(machine, () => conn.hub.markSignedOut()),
      hub: new MachineHub(machine, () => this.publish(), () => void this.refetch(conn)),
    };
    conn.hub.subscribeSessions((event) => {
      const next = applySessionsEvent(conn.items, event);
      if (next.list !== conn.items) {
        conn.items = next.list;
        this.publish();
      }
      if (next.refetch) this.refetchSoon(conn);
    });
    if (this.foreground) conn.hub.start();
    return conn;
  }

  private drop(conn: Conn) {
    if (conn.fetchTimer) clearTimeout(conn.fetchTimer);
    void conn.hub.stop();
  }

  private refetchSoon(conn: Conn) {
    if (conn.fetchTimer) return;
    conn.fetchTimer = setTimeout(() => {
      conn.fetchTimer = null;
      void this.refetch(conn);
    }, 400);
  }

  async refetch(conn: Conn): Promise<void> {
    try {
      conn.items = await conn.api.sessions();
    } catch {
      // A 401 already marked the machine signed out; otherwise the list stays as it was.
    }
    conn.loaded = true;
    this.publish();
  }

  /** Pull to refresh. */
  async refresh(): Promise<void> {
    await Promise.all([...this.conns.values()].map((conn) => this.refetch(conn)));
  }

  /** The app moved to the background (stop the sockets) or came back (start them, read the lists again). */
  setForeground(foreground: boolean) {
    this.foreground = foreground;
    for (const conn of this.conns.values()) {
      if (foreground) {
        conn.hub.start();
        void this.refetch(conn);
      } else {
        void conn.hub.stop();
      }
    }
  }

  hubFor(machineId: string): MachineHub | undefined {
    return this.conns.get(machineId)?.hub;
  }

  apiFor(machineId: string): MachineApi | undefined {
    return this.conns.get(machineId)?.api;
  }

  /**
   * The session's activity as the list has it, kept live by the sessions topic. A session's own stream folds
   * `waiting_input` into idle when it starts from a snapshot, so a wait (a question, a pick on a page) is read here.
   */
  activityOf(machineId: string, sessionId: string): string | undefined {
    return this.conns.get(machineId)?.items.find((item) => item.session.id === sessionId)?.activityStatus ?? undefined;
  }

  private publish() {
    const conns = [...this.conns.values()];
    this.cached = {
      machines: conns.map((c) => ({ machineId: c.machine.machineId, machineName: c.machine.machineName, baseUrl: c.machine.baseUrl, state: c.hub.state })),
      sections: sectionSessions(conns.map((c) => ({ machineId: c.machine.machineId, machineName: c.machine.machineName, items: c.items }))),
      loading: conns.some((c) => !c.loaded && c.hub.state !== "signed-out" && c.hub.state !== "offline"),
    };
    for (const listener of this.listeners) listener();
  }

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  snapshot = (): FleetSnapshot => this.cached;
}
