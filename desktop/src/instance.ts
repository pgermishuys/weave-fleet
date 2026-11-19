import * as fs from "node:fs/promises";
import * as path from "node:path";

/** What a running Fleet writes to `fleet.instance.json` (FleetInstanceInfo on the server). */
export interface FleetInstance {
  pid: number;
  url: string;
  version: string;
  databasePath: string;
  desktop: boolean;
  startedAt: string;
  /** Whether even this computer needs the machine's key (a `fleet node` does). Missing from older Fleets: false. */
  requiresToken: boolean;
}

/** Parses an instance file, or returns null for anything that isn't one. */
export function parseInstance(json: string): FleetInstance | null {
  let value: unknown;
  try {
    value = JSON.parse(json);
  } catch {
    return null;
  }
  if (typeof value !== "object" || value === null) return null;
  const record = value as Record<string, unknown>;
  if (typeof record.pid !== "number" || !Number.isInteger(record.pid) || record.pid <= 0) return null;
  if (typeof record.url !== "string" || !isLoopbackHttpUrl(record.url)) return null;
  return {
    pid: record.pid,
    url: record.url.replace(/\/+$/, ""),
    version: typeof record.version === "string" ? record.version : "unknown",
    databasePath: typeof record.databasePath === "string" ? record.databasePath : "",
    desktop: record.desktop === true,
    startedAt: typeof record.startedAt === "string" ? record.startedAt : "",
    requiresToken: record.requiresToken === true,
  };
}

/** Only loopback URLs: the file sits in a user-writable directory, and the app must never load a remote page from it. */
export function isLoopbackHttpUrl(value: string): boolean {
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return false;
  }
  return url.protocol === "http:" && (url.hostname === "127.0.0.1" || url.hostname === "[::1]" || url.hostname === "localhost");
}

export async function readInstance(file: string): Promise<FleetInstance | null> {
  try {
    return parseInstance(await fs.readFile(file, "utf8"));
  } catch {
    return null;
  }
}

export function isProcessAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    // EPERM: it exists but belongs to someone else.
    return (error as NodeJS.ErrnoException).code === "EPERM";
  }
}

/** Whether the Fleet at `url` answers `/readyz` within `timeoutMs`. */
export async function isReady(url: string, timeoutMs = 1500): Promise<boolean> {
  try {
    const response = await fetch(`${url}/readyz`, { signal: AbortSignal.timeout(timeoutMs) });
    return response.ok;
  } catch {
    return false;
  }
}

/** The Fleet described by the instance file, if it's still running and answering. */
export async function findRunningFleet(instanceFile: string): Promise<FleetInstance | null> {
  const instance = await readInstance(instanceFile);
  if (!instance || !isProcessAlive(instance.pid)) return null;
  return (await isReady(instance.url)) ? instance : null;
}

/** Fleet's smallest access token (MachineIdentityStore.MinimumTokenLength), so a half-written file isn't taken as a key. */
const MINIMUM_TOKEN_LENGTH = 32;

/** The machine's identity file, next to its database: `fleet.db` has `fleet.machine.json` (MachineIdentityStore). */
export function machineFileFor(databasePath: string): string {
  const { dir, name } = path.parse(databasePath);
  return path.join(dir, `${name}.machine.json`);
}

/** The access token in a machine identity file, or null when it holds none. */
export function parseAccessToken(json: string): string | null {
  try {
    const value: unknown = JSON.parse(json);
    const token = typeof value === "object" && value !== null ? (value as Record<string, unknown>).accessToken : undefined;
    return typeof token === "string" && token.length >= MINIMUM_TOKEN_LENGTH ? token : null;
  } catch {
    return null;
  }
}

/** The key of the Fleet running here, read as the user that runs it (the file is the user's own). Null if unreadable. */
export async function readAccessToken(databasePath: string): Promise<string | null> {
  try {
    return parseAccessToken(await fs.readFile(machineFileFor(databasePath), "utf8"));
  } catch {
    return null;
  }
}

/**
 * Signs `fetchImpl`'s cookie jar in to the Fleet at `url` with its key (`POST /auth/token-login`), so the window can
 * open it without the page asking for one. Resolves to whether Fleet accepted the key.
 */
export async function signIn(url: string, token: string, fetchImpl: typeof fetch = fetch): Promise<boolean> {
  try {
    const response = await fetchImpl(`${url}/auth/token-login`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ token }),
      signal: AbortSignal.timeout(5000),
    });
    return response.ok;
  } catch {
    return false;
  }
}
