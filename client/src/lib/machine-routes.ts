/**
 * One machine, several addresses: which one this tab talks to a machine on.
 *
 * A machine reports where it can be reached (its Tailscale name and IP, its local network addresses, its host name)
 * and home keeps the list in the order to try them: Tailscale first because it's encrypted, the local network only
 * when Tailscale doesn't answer. The owner can drag them into another order in Settings → Machines.
 *
 * This tab picks its own address from that list, because it may sit on another network than home: it starts with
 * the one that worked last, and when that doesn't answer it tries the others in order, a quick probe each, the next
 * starting before the last gives up. Every probe asks who answers (`GET /api/machine/id`, no token) and only an
 * address where the machine's own id answers is used, so the token never goes to a device that took over an old
 * address. `baseUrlOf` is what every request and socket uses; a pick changes it for all of them at once.
 *
 * No Vue here, and only types from `@/lib/machines`: it sits under the transport every request path shares.
 */

import type { MachineConnection } from "@/lib/machines";

export type MachineAddressKind = "tailnet" | "tailnet-ip" | "lan" | "hostname" | "other";

/** What came of the last try of an address. */
export type MachineAddressResult = "ok" | "no-answer" | "other-machine";

/** One of a machine's addresses, as home lists it. */
export interface MachineAddress {
  /** Base URL without a trailing slash. */
  url: string;
  kind: MachineAddressKind;
  /** The machine reported it; false for the address it was added with. */
  learned: boolean;
  /** What came of home's last try, when it has tried. */
  lastResult?: MachineAddressResult | null;
  lastTriedAt?: string | null;
}

/** What answered at an address: the machine, nothing, another Fleet, or something that can't say who it is. */
export type ProbeOutcome = "ok" | "no-answer" | "other-machine" | "no-probe";

export const PROBE_TIMEOUT_MS = 3000;
export const PROBE_GAP_MS = 750;
/** How often a connection on a lower address looks for a better one. */
export const RECHECK_MS = 60_000;

// ─── Kinds and order ──────────────────────────────────────────────────────────

/** Tailscale's 100.64.0.0/10. */
function isTailnetIpv4(host: string): boolean {
  const parts = host.split(".").map(Number);
  return parts.length === 4 && parts[0] === 100 && parts[1] >= 64 && parts[1] <= 127;
}

function isPrivateIpv4(host: string): boolean {
  const parts = host.split(".").map(Number);
  if (parts.length !== 4 || parts.some((part) => !Number.isInteger(part) || part < 0 || part > 255)) return false;
  return parts[0] === 10 || (parts[0] === 172 && parts[1] >= 16 && parts[1] <= 31) || (parts[0] === 192 && parts[1] === 168);
}

/** What kind of address `url` is, from its host alone; the same rule home uses. */
export function kindOf(url: string): MachineAddressKind {
  let host: string;
  try {
    host = new URL(url).hostname.toLowerCase();
  } catch {
    return "other";
  }
  if (host.startsWith("[")) {
    const v6 = host.slice(1, -1);
    if (v6.startsWith("fd7a:115c:a1e0:")) return "tailnet-ip";
    return /^f[cd][0-9a-f]{2}:/.test(v6) ? "lan" : "other";
  }
  if (/^\d+\.\d+\.\d+\.\d+$/.test(host)) {
    if (isTailnetIpv4(host)) return "tailnet-ip";
    return isPrivateIpv4(host) ? "lan" : "other";
  }
  if (host.endsWith(".ts.net")) return "tailnet";
  if (!host.includes(".") && host !== "localhost") return "hostname";
  if (host.endsWith(".local") || host.endsWith(".lan") || host.endsWith(".home.arpa")) return "hostname";
  return "other";
}

/** What Settings calls each kind. */
export const KIND_LABELS: Record<MachineAddressKind, string> = {
  "tailnet": "Tailscale",
  "tailnet-ip": "Tailscale IP",
  "lan": "Local network",
  "hostname": "Host name",
  "other": "Address",
};

/** Whether an address goes over Tailscale. */
export function isTailscale(kind: MachineAddressKind): boolean {
  return kind === "tailnet" || kind === "tailnet-ip";
}

/** The list with the address at `from` moved to `to`: dragging, or Alt+↑/↓ on a row. */
export function moveAddress<T>(list: readonly T[], from: number, to: number): T[] {
  const next = [...list];
  if (from < 0 || from >= next.length) return next;
  const [moved] = next.splice(from, 1);
  next.splice(Math.max(0, Math.min(to, next.length)), 0, moved);
  return next;
}

// ─── The address in use ───────────────────────────────────────────────────────

const PICKS_KEY = "weave:machine-addresses";

type Addressed = Pick<MachineConnection, "id" | "baseUrl"> & { addresses?: readonly MachineAddress[] | null };

let picks: Record<string, string> | null = null;
const listeners = new Set<(machineId: string, url: string) => void>();

function loadPicks(): Record<string, string> {
  if (picks) return picks;
  try {
    const raw = typeof window === "undefined" ? null : window.localStorage.getItem(PICKS_KEY);
    const parsed = raw ? JSON.parse(raw) as unknown : null;
    picks = parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed as Record<string, string> : {};
  } catch {
    picks = {};
  }
  return picks;
}

function urlsOf(machine: Addressed): string[] {
  return (machine.addresses ?? []).map((address) => address.url);
}

/**
 * The address this tab uses for `machine`: the one it picked last, while it's still one of the machine's
 * addresses; otherwise the one home uses.
 */
export function baseUrlOf(machine: Addressed): string {
  const picked = loadPicks()[machine.id];
  return picked && urlsOf(machine).includes(picked) ? picked : machine.baseUrl;
}

/** Whether `machine` has more than one address to choose from. */
export function hasChoices(machine: Addressed): boolean {
  return urlsOf(machine).length > 1;
}

/** Whether the address in use is the machine's first, so there's nothing better to look for. */
export function isBestAddress(machine: Addressed): boolean {
  const urls = urlsOf(machine);
  return urls.length < 2 || urls[0] === baseUrlOf(machine);
}

/** Makes `url` the address this tab uses for the machine, and remembers it for next time. */
export function useAddress(machineId: string, url: string): void {
  const current = loadPicks();
  if (current[machineId] === url) return;
  picks = { ...current, [machineId]: url };
  try {
    window.localStorage.setItem(PICKS_KEY, JSON.stringify(picks));
  } catch {
    // Private windows: the pick lasts until the page closes.
  }
  for (const listener of listeners) listener(machineId, url);
}

/** Told when a machine's address in use changes. */
export function onAddressChange(listener: (machineId: string, url: string) => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

// ─── Probing and picking ──────────────────────────────────────────────────────

/** Asks who answers at `url`, without a token. */
export async function probeAddress(url: string, machineId: string, timeoutMs = PROBE_TIMEOUT_MS, signal?: AbortSignal): Promise<ProbeOutcome> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  const stop = () => controller.abort();
  signal?.addEventListener("abort", stop);
  try {
    const response = await fetch(`${url}/api/machine/id`, { credentials: "omit", signal: controller.signal, cache: "no-store" });
    if (!response.ok || !(response.headers.get("content-type") ?? "").includes("application/json")) return "no-probe";
    const body = await response.json() as { id?: unknown };
    if (typeof body?.id !== "string") return "no-probe";
    return body.id === machineId ? "ok" : "other-machine";
  } catch {
    return "no-answer";
  } finally {
    clearTimeout(timer);
    signal?.removeEventListener("abort", stop);
  }
}

export type Probe = (url: string, signal: AbortSignal) => Promise<ProbeOutcome>;

/**
 * Probes `urls` in order, each starting `gapMs` after the one before or as soon as every one before it has failed,
 * and gives the first in the order where the machine answered: a later one that answers sooner waits for those
 * above it. Only "ok" counts; something that can't say who it is isn't trusted at an address it wasn't added at.
 */
export async function raceAddresses(urls: readonly string[], probe: Probe, gapMs = PROBE_GAP_MS): Promise<{ winner: string | null; outcomes: Record<string, ProbeOutcome> }> {
  const outcomes: Record<string, ProbeOutcome> = {};
  if (urls.length === 0) return { winner: null, outcomes };
  const controller = new AbortController();

  return new Promise((resolve) => {
    let started = 0;
    let gapTimer: ReturnType<typeof setTimeout> | null = null;
    let done = false;

    const finish = (winner: string | null) => {
      if (done) return;
      done = true;
      if (gapTimer) clearTimeout(gapTimer);
      controller.abort();
      resolve({ winner, outcomes });
    };

    const decide = () => {
      for (let i = 0; i < urls.length; i++) {
        const outcome = outcomes[urls[i]];
        if (outcome === undefined) {
          // Undecided: wait for it, or start it now that everything above has failed.
          if (i >= started) start();
          return;
        }
        if (outcome === "ok") return finish(urls[i]);
      }
      finish(null);
    };

    const start = () => {
      if (done || started >= urls.length) return;
      const url = urls[started++];
      if (gapTimer) clearTimeout(gapTimer);
      gapTimer = started < urls.length ? setTimeout(start, gapMs) : null;
      probe(url, controller.signal)
        .catch((): ProbeOutcome => "no-answer")
        .then((outcome) => {
          if (done) return;
          outcomes[url] = outcome;
          decide();
        });
    };

    start();
  });
}

const picking = new Map<string, Promise<string>>();

/**
 * The address to talk to `machine` on: the one in use when it answers as this machine, else the first in the order
 * that does (and it becomes the one in use), else the one in use, so the request that follows says why it can't.
 * One pick per machine at a time; callers that ask meanwhile share it.
 */
export function pickAddress(machine: Addressed, probe: Probe = (url, signal) => probeAddress(url, machine.id, PROBE_TIMEOUT_MS, signal)): Promise<string> {
  if (!hasChoices(machine)) return Promise.resolve(baseUrlOf(machine));
  const running = picking.get(machine.id);
  if (running) return running;

  const pick = (async () => {
    const current = baseUrlOf(machine);
    const outcome = await probe(current, new AbortController().signal);
    // The address it was added at is trusted as before when the Fleet there is too old to say who it is.
    if (outcome === "ok" || (outcome === "no-probe" && current === machine.baseUrl)) return current;
    const { winner } = await raceAddresses(urlsOf(machine).filter((url) => url !== current), probe);
    if (!winner) return current;
    useAddress(machine.id, winner);
    return winner;
  })().finally(() => picking.delete(machine.id));
  picking.set(machine.id, pick);
  return pick;
}

/**
 * An address above the one in use where the machine answers now, or null. Nothing moves: the caller moves
 * (`useAddress`) once its connection there is up.
 */
export async function betterAddress(machine: Addressed, probe: Probe = (url, signal) => probeAddress(url, machine.id, PROBE_TIMEOUT_MS, signal)): Promise<string | null> {
  const urls = urlsOf(machine);
  const inUse = urls.indexOf(baseUrlOf(machine));
  if (inUse <= 0) return null;
  const above = urls.slice(0, inUse);
  return (await raceAddresses(above, probe)).winner;
}

/** For a request that failed to reach `machine`: picks again, and says whether it moved to another address. */
export async function repickAddress(machine: Addressed): Promise<boolean> {
  if (!hasChoices(machine)) return false;
  const before = baseUrlOf(machine);
  return (await pickAddress(machine)) !== before;
}

export function _resetForTesting(): void {
  picks = null;
  picking.clear();
  listeners.clear();
}
