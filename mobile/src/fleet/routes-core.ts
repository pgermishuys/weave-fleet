// One machine, several addresses, on the phone: the order to try them in, what the machine reports, and which one to
// use. The same rules as the browser and home (client/src/lib/machine-routes.ts, MachineAddresses.cs): Tailscale
// first, the local network only when Tailscale doesn't answer, a quick probe of each in order, and the token only
// ever goes where the machine's own id answered. No React Native in here.
import {
  isTailscale, kindOf, PROBE_TIMEOUT_MS, probeAddress, raceAddresses,
  type MachineAddress, type MachineAddressKind, type Probe, type ProbeOutcome,
} from "@fleet/lib/machine-routes";

export { isTailscale, KIND_LABELS, kindOf, moveAddress, PROBE_GAP_MS, PROBE_TIMEOUT_MS, raceAddresses, RECHECK_MS } from "@fleet/lib/machine-routes";
export type { MachineAddress, MachineAddressKind, MachineAddressResult, Probe, ProbeOutcome } from "@fleet/lib/machine-routes";

/** Where a kind goes when nobody has said otherwise; lower is tried first. */
function rankOf(kind: MachineAddressKind): number {
  return { "tailnet": 0, "tailnet-ip": 1, "lan": 2, "hostname": 3, "other": 4 }[kind];
}

const trim = (url: string) => url.trim().replace(/\/+$/, "");
const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

/**
 * Whether the phone may use `url`. https always. Plain http only to this device (the walk-through's Fleet), a
 * Tailscale address (Tailscale encrypts it), or a private address on the local network, the one place the browser and
 * home send a token over plain http too. A host name could resolve anywhere, so the phone doesn't use it over http.
 */
export function canUse(url: string): boolean {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return false;
  }
  if (parsed.protocol === "https:") return true;
  if (parsed.protocol !== "http:") return false;
  if (/^(localhost|127\.0\.0\.1|\[::1\])$/i.test(parsed.hostname)) return true;
  const kind = kindOf(url);
  return isTailscale(kind) || kind === "lan";
}

/** A machine's list when the phone first pairs with it: the address it paired at. */
export function seedAddresses(url: string): MachineAddress[] {
  return [{ url: trim(url), kind: kindOf(url), learned: false }];
}

/** After the last address of its own or an earlier kind, so it never jumps above one the owner moved up. */
function insert(list: (MachineAddress | null)[], address: MachineAddress): (MachineAddress | null)[] {
  const rank = rankOf(address.kind);
  let last = -1;
  list.forEach((other, i) => {
    if (other && rankOf(other.kind) <= rank) last = i;
  });
  return [...list.slice(0, last + 1), address, ...list.slice(last + 1)];
}

/**
 * The list after the machine reported `reported` as where it answers now. Learned addresses it no longer reports
 * drop away (not the one in use); a new one of the same kind takes the place of one that went (a new DHCP lease
 * keeps the order); any other new one goes in at its kind's place. The address the phone paired at stays.
 */
export function learnAddresses(current: readonly MachineAddress[], reported: readonly string[], inUse: string): MachineAddress[] {
  let list: (MachineAddress | null)[] = current.some((a) => same(a.url, inUse)) ? [...current] : insert([...current], { url: inUse, kind: kindOf(inUse), learned: false });
  const fresh: string[] = [];
  for (const url of reported.map(trim)) if (url && !fresh.some((f) => same(f, url))) fresh.push(url);

  const freed: { index: number; kind: MachineAddressKind }[] = [];
  list.forEach((address, index) => {
    if (address?.learned && !same(address.url, inUse) && !fresh.some((url) => same(url, address.url))) {
      freed.push({ index, kind: address.kind });
      list[index] = null;
    }
  });

  for (const url of fresh) {
    if (list.some((address) => address && same(address.url, url))) continue;
    const learned: MachineAddress = { url, kind: kindOf(url), learned: true };
    const slot = freed.findIndex((f) => f.kind === learned.kind);
    if (slot >= 0) {
      list[freed[slot].index] = learned;
      freed.splice(slot, 1);
    } else {
      list = insert(list, learned);
    }
  }
  return list.filter((address): address is MachineAddress => address !== null);
}

/** The list in the given order, or null when `urls` isn't exactly its addresses. */
export function reorderAddresses(current: readonly MachineAddress[], urls: readonly string[]): MachineAddress[] | null {
  if (urls.length !== current.length) return null;
  const next: MachineAddress[] = [];
  for (const url of urls) {
    const match = current.find((address) => same(address.url, url));
    if (!match || next.includes(match)) return null;
    next.push(match);
  }
  return next;
}

/**
 * The phone's list with the addresses home also lists put in home's order (the order the owner dragged them into
 * on the computer). Addresses only the phone has, like the https one it paired at, keep their places.
 */
export function adoptOrder(current: readonly MachineAddress[], homeOrder: readonly string[]): MachineAddress[] {
  const shared = homeOrder.map((url) => current.find((address) => same(address.url, url))).filter((a): a is MachineAddress => !!a);
  let next = 0;
  return current.map((address) => (shared.includes(address) ? shared[next++] : address));
}

/** The order to save on home: home's addresses as the phone now orders them, any the phone doesn't have kept last. */
export function orderForHome(phone: readonly MachineAddress[], homeOrder: readonly string[]): string[] {
  const at = (url: string) => {
    const index = phone.findIndex((address) => same(address.url, url));
    return index < 0 ? phone.length : index;
  };
  return [...homeOrder].map((url, i) => ({ url, i })).sort((a, b) => at(a.url) - at(b.url) || a.i - b.i).map((x) => x.url);
}

/** The list with what came of each probe, for the machine screen. */
export function recordOutcomes(current: readonly MachineAddress[], outcomes: Record<string, ProbeOutcome>, at = new Date()): MachineAddress[] {
  return current.map((address) => {
    const outcome = outcomes[address.url];
    if (!outcome) return address;
    const lastResult = outcome === "ok" ? "ok" : outcome === "other-machine" ? "other-machine" : "no-answer";
    return { ...address, lastResult, lastTriedAt: at.toISOString() };
  });
}

/** The address the phone paired at: trusted as before when the Fleet there is too old to say who it is. */
export function pairedUrl(addresses: readonly MachineAddress[], fallback: string): string {
  return addresses.find((address) => !address.learned)?.url ?? fallback;
}

export interface Pick {
  /** The address to use: the winner, or the one in use when nothing else answered as this machine. */
  url: string;
  outcomes: Record<string, ProbeOutcome>;
}

export const probeFor = (machineId: string): Probe => (url, signal) => probeAddress(url, machineId, PROBE_TIMEOUT_MS, signal);

/**
 * Where to talk to the machine: the address in use when it answers as this machine, else the first in the order that
 * does, else the one in use (so the request that follows says why it can't).
 */
export async function pickAddress(addresses: readonly MachineAddress[], inUse: string, probe: Probe): Promise<Pick> {
  const outcome = await probe(inUse, new AbortController().signal);
  const outcomes: Record<string, ProbeOutcome> = { [inUse]: outcome };
  if (outcome === "ok" || (outcome === "no-probe" && same(inUse, pairedUrl(addresses, inUse)))) return { url: inUse, outcomes };
  const others = addresses.map((a) => a.url).filter((url) => !same(url, inUse) && canUse(url));
  const race = await raceAddresses(others, probe);
  return { url: race.winner ?? inUse, outcomes: { ...outcomes, ...race.outcomes } };
}

/**
 * An address above the one in use where the machine answers now (`winner`, else null), and what each probe found.
 * Nothing moves: the caller moves once its connection there is up.
 */
export async function betterAddress(addresses: readonly MachineAddress[], inUse: string, probe: Probe): Promise<{ winner: string | null; outcomes: Record<string, ProbeOutcome> }> {
  const index = addresses.findIndex((address) => same(address.url, inUse));
  const above = index < 0 ? [] : addresses.slice(0, index).map((a) => a.url).filter(canUse);
  return raceAddresses(above, probe);
}

/** Whether the address in use is the first one the phone can use, so there's nothing better to look for. */
export function isBest(addresses: readonly MachineAddress[], inUse: string): boolean {
  const first = addresses.find((address) => canUse(address.url));
  return !first || same(first.url, inUse);
}

/** How the phone is connected, from the kind of the address in use. */
export function routeLine(addresses: readonly MachineAddress[], inUse: string): { text: string; note: string | null } {
  const kind = kindOf(inUse);
  const text = isTailscale(kind) ? "Connected over Tailscale"
    : kind === "lan" ? "Connected on your network"
      : kind === "hostname" ? "Connected by host name"
        : "Connected";
  const first = addresses[0];
  // Only when the phone fell back off Tailscale: on its IP after its name failed, Tailscale itself works.
  const note = first && !same(first.url, inUse) && isTailscale(first.kind) && !isTailscale(kind) ? "Tailscale isn't answering" : null;
  return { text, note };
}
