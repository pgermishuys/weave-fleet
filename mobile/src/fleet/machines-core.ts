// The phone's list of paired machines. Pure: the storage (Keychain/Keystore on a phone) comes in from outside.
import type { MachineAddress } from "~/fleet/routes-core";

export interface Machine {
  machineId: string;
  machineName: string;
  /** The address in use: the one that answered as this machine last (see routes-core). Every request and socket uses it. */
  baseUrl: string;
  /** Every address the machine can be reached at, in the order to try them. Missing until it first reports them. */
  addresses?: MachineAddress[];
  /** The owner put the addresses in an order on this phone, so home's order no longer replaces it. */
  ownOrder?: boolean;
  token: string;
  deviceId: string;
  pairedAt: number;
}

export interface KeyValueStorage {
  get(key: string): Promise<string | null>;
  set(key: string, value: string): Promise<void>;
  remove(key: string): Promise<void>;
}

const INDEX_KEY = "fleet.machines.v1";
// SecureStore keys allow letters, digits, ".", "-" and "_".
const keyOf = (machineId: string) => `fleet.machine.${machineId.replace(/[^A-Za-z0-9._-]/g, "_")}`;

/** Each machine's device key lives under its own storage key, so forgetting a machine deletes exactly that key. */
export class MachinesStore {
  private list: Machine[] = [];
  private listeners = new Set<() => void>();
  loaded = false;

  constructor(private storage: KeyValueStorage) {}

  async load(): Promise<Machine[]> {
    const ids = JSON.parse((await this.storage.get(INDEX_KEY)) ?? "[]") as string[];
    const found = await Promise.all(ids.map(async (id) => {
      const raw = await this.storage.get(keyOf(id));
      return raw ? (JSON.parse(raw) as Machine) : null;
    }));
    this.list = found.filter((machine): machine is Machine => machine !== null);
    this.loaded = true;
    this.emit();
    return this.list;
  }

  all(): Machine[] {
    return this.list;
  }

  get(machineId: string): Machine | undefined {
    return this.list.find((machine) => machine.machineId === machineId);
  }

  /** Pairing the same machine again replaces its entry (and so its key). */
  async add(machine: Machine): Promise<void> {
    await this.storage.set(keyOf(machine.machineId), JSON.stringify(machine));
    this.list = [...this.list.filter((m) => m.machineId !== machine.machineId), machine];
    await this.storage.set(INDEX_KEY, JSON.stringify(this.list.map((m) => m.machineId)));
    this.emit();
  }

  /** Saves a change to a paired machine's address in use or its addresses. Nothing happens for one it forgot meanwhile. */
  async update(machineId: string, patch: Partial<Pick<Machine, "baseUrl" | "addresses" | "ownOrder">>): Promise<void> {
    const current = this.get(machineId);
    if (!current) return;
    const next = { ...current, ...patch };
    this.list = this.list.map((m) => (m.machineId === machineId ? next : m));
    await this.storage.set(keyOf(machineId), JSON.stringify(next));
    this.emit();
  }

  async remove(machineId: string): Promise<void> {
    this.list = this.list.filter((m) => m.machineId !== machineId);
    await this.storage.set(INDEX_KEY, JSON.stringify(this.list.map((m) => m.machineId)));
    await this.storage.remove(keyOf(machineId));
    this.emit();
  }

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  private emit() {
    for (const listener of this.listeners) listener();
  }
}
