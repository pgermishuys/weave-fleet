import * as fs from "node:fs/promises";

/** What a running Fleet writes to `fleet.instance.json` (FleetInstanceInfo on the server). */
export interface FleetInstance {
  pid: number;
  url: string;
  version: string;
  databasePath: string;
  desktop: boolean;
  startedAt: string;
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
