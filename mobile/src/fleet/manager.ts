// Every paired machine's connection and session list, in one place. No React Native in here: the app tells it when it
// goes to the background (`setForeground`), and hooks.ts reads it.
import { interruptedEntries, interruptionOf, type InterruptedEntry, type Interruption } from "~/fleet/interruption-core";
import { applySessionsEvent, sectionSessions, type SessionSection } from "~/fleet/session-list";
import { machineApi, type MachineApi, type SessionListItem } from "~/fleet/api";
import { MachineHub, type ConnectionState } from "~/fleet/hub";
import type { Machine, MachinesStore } from "~/fleet/machines-core";
import {
  adoptOrder, betterAddress, canUse, isBest, learnAddresses, orderForHome, pickAddress, probeFor, recordOutcomes, RECHECK_MS,
  reorderAddresses, seedAddresses, type MachineAddress, type Probe, type ProbeOutcome,
} from "~/fleet/routes-core";

export interface MachineView {
  machineId: string;
  machineName: string;
  /** The address in use. */
  baseUrl: string;
  /** Every address, in the order to try them, with what came of the phone's last try of each. */
  addresses: MachineAddress[];
  state: ConnectionState;
}

export interface FleetSnapshot {
  machines: MachineView[];
  sections: SessionSection[];
  /** Sessions a restart cut off, on every machine. */
  interrupted: InterruptedEntry[];
  /** True until each machine has answered once. */
  loading: boolean;
}

interface Conn {
  machine: Machine;
  /** What the api and the hub read on every call: a pick changes the address for both at once. */
  route: { baseUrl: string; token: string };
  hub: MachineHub;
  api: MachineApi;
  items: SessionListItem[];
  loaded: boolean;
  fetchTimer: ReturnType<typeof setTimeout> | null;
  /** Every minute while in front: learn the machine's addresses again, and look for a better one. */
  recheckTimer: ReturnType<typeof setInterval> | null;
  picking: Promise<boolean> | null;
  moving: boolean;
  /** What came of the last probe of each address, for the machine screen. */
  outcomes: Record<string, ProbeOutcome>;
  outcomesAt: Date | null;
  /** The paired machine that lists this one as its own (home), and home's order, when there is one. */
  home: { machineId: string; order: string[] } | null;
}

export class FleetManager {
  private conns = new Map<string, Conn>();
  private listeners = new Set<() => void>();
  private foreground = true;
  private cached: FleetSnapshot = { machines: [], sections: [], interrupted: [], loading: false };

  /** `probe` asks who answers at an address, `makeHub` opens a machine's hub; tests replace them. */
  constructor(
    private store: MachinesStore,
    private probe: (machineId: string) => Probe = probeFor,
    private makeHub: (...args: ConstructorParameters<typeof MachineHub>) => MachineHub = (...args) => new MachineHub(...args),
  ) {
    store.subscribe(() => this.sync());
    this.sync();
  }

  /** Make the connections match the paired machines. */
  private sync() {
    const paired = this.store.all();
    for (const [id, conn] of this.conns) {
      const now = paired.find((m) => m.machineId === id);
      // A new address in use or a new order keeps the connection: only pairing again (a new key) starts over.
      if (now && now.token === conn.machine.token) {
        conn.machine = now;
        continue;
      }
      this.drop(conn);
      this.conns.delete(id);
    }
    for (const machine of paired) if (!this.conns.has(machine.machineId)) this.conns.set(machine.machineId, this.connect(machine));
    this.publish();
  }

  private connect(machine: Machine): Conn {
    const route = { baseUrl: machine.baseUrl, token: machine.token };
    const conn: Conn = {
      machine,
      route,
      items: [],
      loaded: false,
      fetchTimer: null,
      recheckTimer: null,
      picking: null,
      moving: false,
      outcomes: {},
      outcomesAt: null,
      home: null,
      api: machineApi(route, () => conn.hub.markSignedOut(), () => this.pick(conn)),
      // Each time it's (back) up: read the list, learn the addresses, and look for a better one (the phone may have
      // joined another network while it was in the background).
      hub: this.makeHub(route, () => this.publish(), () => {
        void this.refetch(conn);
        void this.learn(conn).then(() => this.tryBetter(conn));
      }, async () => void (await this.pick(conn))),
    };
    conn.hub.subscribeSessions((event) => {
      const next = applySessionsEvent(conn.items, event);
      if (next.list !== conn.items) {
        conn.items = next.list;
        this.publish();
      }
      if (next.refetch) this.refetchSoon(conn);
    });
    if (this.foreground) this.resume(conn);
    return conn;
  }

  private resume(conn: Conn) {
    conn.hub.start();
    if (!conn.recheckTimer) conn.recheckTimer = setInterval(() => void this.recheck(conn), RECHECK_MS);
  }

  private pause(conn: Conn) {
    if (conn.recheckTimer) clearInterval(conn.recheckTimer);
    conn.recheckTimer = null;
    void conn.hub.stop();
  }

  private drop(conn: Conn) {
    if (conn.fetchTimer) clearTimeout(conn.fetchTimer);
    this.pause(conn);
  }

  // ─── Addresses ────────────────────────────────────────────────────────────

  private addressesOf(conn: Conn): MachineAddress[] {
    return conn.machine.addresses ?? seedAddresses(conn.route.baseUrl);
  }

  private remember(conn: Conn, outcomes: Record<string, ProbeOutcome>) {
    if (Object.keys(outcomes).length === 0) return;
    conn.outcomes = { ...conn.outcomes, ...outcomes };
    conn.outcomesAt = new Date();
    this.publish();
  }

  /** Makes `url` the address in use for the api and the hub's next connect, and remembers it for next time. */
  private async use(conn: Conn, url: string) {
    conn.route.baseUrl = url;
    this.publish();
    await this.store.update(conn.machine.machineId, { baseUrl: url });
  }

  /**
   * Picks the address to connect on: the one in use when it answers as this machine, else the first in the order
   * that does. One pick per machine at a time; callers that ask meanwhile share it. True when it moved.
   */
  pick(conn: Conn): Promise<boolean> {
    const addresses = this.addressesOf(conn);
    if (addresses.filter((a) => canUse(a.url)).length < 2) return Promise.resolve(false);
    if (conn.picking) return conn.picking;
    conn.picking = (async () => {
      const before = conn.route.baseUrl;
      const picked = await pickAddress(addresses, before, this.probe(conn.machine.machineId));
      this.remember(conn, picked.outcomes);
      if (picked.url === before || conn.route.baseUrl !== before) return false;
      await this.use(conn, picked.url);
      return true;
    })().finally(() => (conn.picking = null));
    return conn.picking;
  }

  /**
   * Moves to a better address that answers again, once the hub's connection there is up (see `MachineHub.moveTo`);
   * a better address that won't hold one leaves the working connection alone.
   */
  async tryBetter(conn: Conn): Promise<void> {
    const addresses = this.addressesOf(conn);
    if (conn.moving || conn.picking || conn.hub.state !== "connected" || isBest(addresses, conn.route.baseUrl)) return;
    conn.moving = true;
    try {
      const before = conn.route.baseUrl;
      const { winner, outcomes } = await betterAddress(addresses, before, this.probe(conn.machine.machineId));
      this.remember(conn, outcomes);
      if (!winner || conn.route.baseUrl !== before) return;
      if (await conn.hub.moveTo(winner)) await this.use(conn, winner);
    } finally {
      conn.moving = false;
    }
  }

  /**
   * Learns where the machine answers now (it reports its addresses itself), and the owner's order from home when the
   * phone is paired with that too.
   */
  async learn(conn: Conn): Promise<void> {
    const id = conn.machine.machineId;
    const reported = await conn.api.machine().then((m) => m.addresses?.map((a) => a.url) ?? null, () => null);
    let home: Conn["home"] = null;
    for (const other of this.conns.values()) {
      if (other === conn || other.hub.state !== "connected") continue;
      const listed = await other.api.machines().then((r) => r.machines.find((m) => m.id === id), () => undefined);
      if (!listed?.addresses?.length) continue;
      home = { machineId: other.machine.machineId, order: listed.addresses.map((a) => a.url) };
      break;
    }
    conn.home = home;

    // From the list as it is now: the owner may have reordered it while the reads were out.
    const current = this.addressesOf(conn);
    let addresses = reported ? learnAddresses(current, reported, conn.route.baseUrl) : current;
    if (home && !conn.machine.ownOrder) addresses = adoptOrder(addresses, home.order);
    if (JSON.stringify(addresses) !== JSON.stringify(current)) {
      await this.store.update(id, { addresses });
      this.publish();
    }
  }

  private async recheck(conn: Conn) {
    if (conn.hub.state !== "connected") return;
    await this.learn(conn);
    await this.tryBetter(conn);
  }

  /**
   * Puts a machine's addresses in the owner's order: kept on the phone, and saved on its home too when the phone is
   * paired with that and home takes it. Throws when the list changed meanwhile.
   */
  async reorder(machineId: string, urls: string[]): Promise<void> {
    const conn = this.conns.get(machineId);
    if (!conn) return;
    const addresses = reorderAddresses(this.addressesOf(conn), urls);
    if (!addresses) throw new Error("The addresses changed meanwhile. Try again.");
    let savedOnHome = false;
    const home = conn.home && this.conns.get(conn.home.machineId);
    if (conn.home && home) {
      const order = orderForHome(addresses, conn.home.order);
      savedOnHome = await home.api.reorderAddresses(machineId, order).then(() => true, () => false);
      if (savedOnHome) conn.home = { ...conn.home, order };
    }
    // Home's order would put it back on the next read when home didn't take it.
    await this.store.update(machineId, { addresses, ownOrder: !savedOnHome });
    this.publish();
    void this.tryBetter(conn);
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
        this.resume(conn);
        void this.refetch(conn);
      } else {
        this.pause(conn);
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

  /** The session's cut-off mark as the list has it, kept live by `session.interrupted`. */
  interruptionOf(machineId: string, sessionId: string): Interruption | null {
    const item = this.conns.get(machineId)?.items.find((i) => i.session.id === sessionId);
    return item ? interruptionOf(item) : null;
  }

  /** Stop all: every machine with sessions Fleet is continuing by itself stops doing so. */
  async stopAutomatic(): Promise<void> {
    const auto = [...this.conns.values()].filter((c) => interruptedEntries([{ machineId: "", machineName: "", items: c.items }]).some((e) => e.interruption.auto));
    await Promise.all(auto.map((c) => c.api.stopAutomatic()));
    await Promise.all(auto.map((c) => this.refetch(c)));
  }

  private publish() {
    const conns = [...this.conns.values()];
    this.cached = {
      interrupted: interruptedEntries(conns.map((c) => ({ machineId: c.machine.machineId, machineName: c.machine.machineName, items: c.items }))),
      machines: conns.map((c) => ({
        machineId: c.machine.machineId,
        machineName: c.machine.machineName,
        baseUrl: c.route.baseUrl,
        addresses: c.outcomesAt ? recordOutcomes(this.addressesOf(c), c.outcomes, c.outcomesAt) : this.addressesOf(c),
        state: c.hub.state,
      })),
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
