/**
 * Which machines a paired phone still needs its own token for, and asking its home machine for one. No Vue.
 */
import type { DeviceCredentials, MachineGrant } from "@/lib/device-credentials";

/** A machine in home's list, as the phone sees it (no token). */
export interface ListedMachine {
  id: string;
  name: string;
  baseUrl: string;
  os?: string | null;
  status?: string;
  lastSeenAt?: string | null;
}

/** Why the phone can't talk to a machine directly, or null when it can. */
export function unreachableReason(machine: ListedMachine, pageProtocol: string): string | null {
  // An https page can't call an http address (mixed content).
  if (pageProtocol === "https:" && machine.baseUrl.startsWith("http://")) {
    return "Reachable only from computers. Add it with its https://….ts.net address.";
  }
  return null;
}

/** The machines the phone could talk to but has no token for yet. Home itself never needs one. */
export function machinesNeedingGrants(
  machines: readonly ListedMachine[],
  credentials: DeviceCredentials,
  pageProtocol: string,
): ListedMachine[] {
  return machines.filter((machine) =>
    machine.id !== credentials.homeMachineId
    && unreachableReason(machine, pageProtocol) === null
    && !credentials.grants.some((grant) => grant.machineId === machine.id));
}

/** Asks home for the phone's own token on `machineId`. Throws with home's reason when it can't. */
export async function requestGrant(machineId: string, homeToken: string | null): Promise<MachineGrant> {
  const headers: Record<string, string> = {};
  if (homeToken) headers.Authorization = `Bearer ${homeToken}`;
  const response = await fetch(`/api/machines/${encodeURIComponent(machineId)}/device-grant`, {
    method: "POST",
    headers,
    credentials: "include",
  });
  if (!response.ok) {
    let message = `Couldn't get a key for that machine (${response.status}).`;
    try {
      message = ((await response.json()) as { error?: string }).error ?? message;
    } catch {
      // Keep the general message.
    }
    throw new Error(message);
  }
  return await response.json() as MachineGrant;
}

/** Reads home's machine list (no tokens for a phone). */
export async function fetchMachineList(homeToken: string | null): Promise<ListedMachine[]> {
  const headers: Record<string, string> = {};
  if (homeToken) headers.Authorization = `Bearer ${homeToken}`;
  const response = await fetch("/api/machines", { headers, credentials: "include" });
  if (!response.ok) return [];
  const body = await response.json() as { machines?: ListedMachine[] };
  return Array.isArray(body.machines) ? body.machines : [];
}
