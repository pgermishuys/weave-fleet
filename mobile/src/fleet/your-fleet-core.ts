// "Your Fleet" on the phone: the machines in the Fleet the phone is paired with, and the keys it asks for.
// Pure: the storage comes in from outside, and so does every call to a machine.
import type { Machine, KeyValueStorage } from "~/fleet/machines-core";
import { canUse, kindOf, seedAddresses, type MachineAddress } from "~/fleet/routes-core";

/** A machine in a paired machine's list, as `GET /api/machines` gives it to a phone (no token). */
export interface ListedMachine {
  id: string;
  name?: string | null;
  baseUrl: string;
  addresses?: { url: string; kind?: string }[] | null;
}

/** What `POST /api/machines/{id}/device-grant` answers: the phone's own key on that machine. */
export interface Grant {
  machineId: string;
  baseUrl: string;
  token: string;
}

/** "atlas joined your Fleet": shown on the Sessions screen until it's dismissed; tapping it opens Machines. */
export interface JoinedNotice {
  machineId: string;
  machineName: string;
  at: number;
}

/** "atlas wants to join your Fleet": a machine that is waiting to be added. Adding it is done on a screen with Fleet's Add a machine. */
export interface WantingNotice {
  machineId: string;
  machineName: string;
}

export const wantingText = (notice: Pick<WantingNotice, "machineName">) => `${notice.machineName} wants to join your Fleet`;

export const joinedText = (notice: Pick<JoinedNotice, "machineName">) => `${notice.machineName} joined your Fleet`;

/**
 * The machines in `listed` the phone has no key for. A machine the phone paired with itself, home, and one the person
 * removed from this phone (`declined`) are left alone.
 */
export function machinesToJoin(listed: readonly ListedMachine[], paired: readonly Machine[], homeId: string, declined: ReadonlySet<string>): ListedMachine[] {
  return listed.filter((m) => m.id !== homeId && !declined.has(m.id) && !paired.some((p) => p.machineId === m.id));
}

/**
 * The machine as the phone keeps it, from home's list and the key home asked it for. The addresses are home's, in the
 * owner's order, minus any the phone mustn't send a key over; none left means the phone can't reach it (null).
 */
export function machineFromGrant(listed: ListedMachine, grant: Grant, homeId: string, now: number): Machine | null {
  const urls = [...(listed.addresses ?? []).map((a) => a.url), listed.baseUrl, grant.baseUrl];
  const usable = [...new Set(urls.filter((url) => url && canUse(url)))];
  if (usable.length === 0) return null;
  const addresses: MachineAddress[] = usable.map((url) => ({ url, kind: kindOf(url), learned: true }));
  const first = canUse(grant.baseUrl) ? grant.baseUrl : usable[0];
  return {
    machineId: listed.id,
    machineName: listed.name?.trim() || "Fleet",
    baseUrl: first,
    addresses: addresses.length > 0 ? addresses : seedAddresses(first),
    token: grant.token,
    // The key's own id stays on the machine that made it; the phone never needs it.
    deviceId: "",
    pairedAt: now,
    via: homeId,
  };
}

interface RosterState {
  /** Every machine id the phone has seen in a list so far. */
  known: string[];
  /** Machines the person removed from this phone: they aren't asked for again. */
  declined: string[];
}

const ROSTER_KEY = "fleet.roster.v1";

/** What the phone remembers about the Fleet's list between runs. Not a secret, but it lives beside the keys. */
export class Roster {
  private state: RosterState | null = null;

  constructor(private storage: KeyValueStorage) {}

  private async read(): Promise<RosterState | null> {
    if (this.state) return this.state;
    const raw = await this.storage.get(ROSTER_KEY);
    if (!raw) return null;
    try {
      this.state = JSON.parse(raw) as RosterState;
    } catch {
      return null;
    }
    return this.state;
  }

  private async write(state: RosterState) {
    this.state = state;
    await this.storage.set(ROSTER_KEY, JSON.stringify(state));
  }

  async declined(): Promise<Set<string>> {
    return new Set((await this.read())?.declined ?? []);
  }

  async decline(machineId: string) {
    const state = (await this.read()) ?? { known: [], declined: [] };
    if (!state.declined.includes(machineId)) await this.write({ ...state, declined: [...state.declined, machineId] });
  }

  /** Pairing a machine again by hand undoes a removal. */
  async undecline(machineId: string) {
    const state = await this.read();
    if (state?.declined.includes(machineId)) await this.write({ ...state, declined: state.declined.filter((id) => id !== machineId) });
  }

  /**
   * Takes in a list the phone just read, and says which of its machines are new since the last one. The first list the
   * phone ever reads has none new: those machines were there before the phone came. A new machine stays new until
   * `joined` says the phone has its key, so one that was away when it was added is still announced when it answers.
   */
  async see(ids: readonly string[]): Promise<Set<string>> {
    const state = await this.read();
    if (!state) {
      await this.write({ known: [...new Set(ids)], declined: [] });
      return new Set();
    }
    const known = new Set(state.known);
    return new Set(ids.filter((id) => !known.has(id)));
  }

  /** The phone has this machine's key now and has said so. */
  async joined(machineId: string) {
    const state = (await this.read()) ?? { known: [], declined: [] };
    if (!state.known.includes(machineId)) await this.write({ ...state, known: [...state.known, machineId] });
  }
}
