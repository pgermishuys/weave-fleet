/**
 * Whether the desktop app lets other devices reach it (`GET`/`PUT /api/machine/reach`). Only the app's own Fleet can
 * change it; any other Fleet answers `available: false`. Always the home machine.
 */
import { fetchOnMachine } from "@/lib/machines";

export type ReachMode = "off" | "tailscale-and-home" | "home" | "own";

export interface ReachAddress {
  url: string;
  kind: "tailnet" | "lan" | "hostname";
}

/** What Fleet found about Tailscale on this machine. `canTurnOn`: Tailscale is fine and only the listener is missing. */
export interface TailscaleCheck {
  state: "missing" | "stopped" | "signed-out" | "running";
  headline: string;
  detail: string;
  /** The reach listener already covers the Tailscale address. */
  covered: boolean;
  canTurnOn: boolean;
}

export interface ReachState {
  /** False for a Fleet that isn't the desktop app's own: nothing can be changed. */
  available: boolean;
  mode: ReachMode;
  ownAddress: string | null;
  /** The loopback port a tunnel or proxy sends to for "my own address"; it always asks for a key. */
  ownPort: number;
  /** Where other devices reach it now, best first; empty while off. */
  listening: ReachAddress[];
  /** The addresses on this machine's networks, Tailscale first: what the choices would use. */
  found: ReachAddress[];
  /** Null for a Fleet that can't change reach; absent on an older one. */
  tailscale?: TailscaleCheck | null;
}

async function readError(response: Response, fallback: string): Promise<string> {
  try {
    const body = await response.json() as { error?: string; message?: string };
    return body.error ?? body.message ?? fallback;
  } catch {
    return fallback;
  }
}

/** Null on a Fleet too old to know, or when this browser may not manage it (a paired device). */
export async function loadReach(): Promise<ReachState | null> {
  const response = await fetchOnMachine(null, "/api/machine/reach");
  if (response.status === 404 || response.status === 403) return null;
  if (!response.ok) throw new Error(await readError(response, "Couldn't read who can reach this machine."));
  return await response.json() as ReachState;
}

export async function saveReach(mode: ReachMode, ownAddress?: string): Promise<ReachState> {
  const response = await fetchOnMachine(null, "/api/machine/reach", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ mode, ownAddress }),
  });
  if (!response.ok) throw new Error(await readError(response, "Couldn't change who can reach this machine."));
  return await response.json() as ReachState;
}
