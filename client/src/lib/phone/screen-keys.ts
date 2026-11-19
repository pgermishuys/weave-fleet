/**
 * Your Fleet (Labs): the keys a screen that isn't a paired phone (a browser, the desktop app's window) holds for the
 * other machines in its home machine's list. They're the same device tokens a phone gets (see `grants.ts`), kept apart
 * from a phone's credentials so a screen never looks paired: no push subscription, no notification answers. No Vue.
 */
import type { DeviceCredentials, MachineGrant } from "@/lib/device-credentials";

interface ScreenKeys {
  screenId: string;
  grants: MachineGrant[];
}

const KEY = "weave:screen-keys";

function storage(): Storage | null {
  try {
    return typeof localStorage === "undefined" ? null : localStorage;
  } catch {
    return null;
  }
}

function newScreenId(): string {
  const bytes = new Uint8Array(12);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0")).join("");
}

function read(): ScreenKeys | null {
  try {
    const parsed: unknown = JSON.parse(storage()?.getItem(KEY) ?? "null");
    if (!parsed || typeof parsed !== "object") return null;
    const candidate = parsed as Partial<ScreenKeys>;
    return typeof candidate.screenId === "string" && Array.isArray(candidate.grants)
      ? { screenId: candidate.screenId, grants: candidate.grants }
      : null;
  } catch {
    return null;
  }
}

function write(keys: ScreenKeys): void {
  storage()?.setItem(KEY, JSON.stringify(keys));
}

/** This screen's id, made the first time and kept; the server tells its keys apart by it. */
export function screenId(): string {
  const existing = read();
  if (existing) return existing.screenId;
  const created = { screenId: newScreenId(), grants: [] };
  write(created);
  return created.screenId;
}

/** The key this screen holds for `machineId`, or null. */
export function screenGrantFor(machineId: string): MachineGrant | null {
  return read()?.grants.find((grant) => grant.machineId === machineId) ?? null;
}

export function saveScreenGrant(grant: MachineGrant): void {
  const current = read() ?? { screenId: screenId(), grants: [] };
  write({ ...current, grants: [...current.grants.filter((g) => g.machineId !== grant.machineId), grant] });
}

export function removeScreenGrant(machineId: string): void {
  const current = read();
  if (current) write({ ...current, grants: current.grants.filter((g) => g.machineId !== machineId) });
}

/** What to call this screen on the machine that gets its key: "Chrome on Windows", say. */
export function screenName(): string {
  const agent = typeof navigator === "undefined" ? "" : navigator.userAgent;
  const browser = /Edg\//.test(agent) ? "Edge" : /Firefox\//.test(agent) ? "Firefox" : /Chrome\//.test(agent) ? "Chrome" : /Safari\//.test(agent) ? "Safari" : "A browser";
  const system = /Windows/.test(agent) ? " on Windows" : /Android/.test(agent) ? " on Android" : /iPhone|iPad/.test(agent) ? " on iOS" : /Mac OS/.test(agent) ? " on a Mac" : /Linux/.test(agent) ? " on Linux" : "";
  return `${browser}${system}`;
}

/** This screen as credentials, so the inbox treats its keys like a phone's: home needs no token, the cookie signs it in. */
export function screenCredentials(home: { id: string; name: string }, origin: string = window.location.origin): DeviceCredentials {
  return {
    homeMachineId: home.id,
    homeMachineName: home.name,
    homeBaseUrl: origin,
    deviceId: screenId(),
    token: "",
    grants: read()?.grants ?? [],
    pairedAt: "",
  };
}
