import { shallowRef } from "vue";
import { readCredentials, removeGrant, saveGrant, type DeviceCredentials } from "@/lib/device-credentials";
import { machinesNeedingGrants, requestGrant, unreachableReason, type ListedMachine } from "@/lib/phone/grants";

/**
 * The phone's keys to other machines. After pairing and whenever the inbox loads, the phone asks home for its own
 * token on every machine in home's list it lacks one for; a machine that turns a token away gets one fresh request.
 * Machines the phone can't reach directly (http on an https page) are skipped with a reason.
 */
export function useMachineGrants() {
  const problems = shallowRef<Record<string, string>>({});
  const retried = new Set<string>();

  async function ensureGrants(machines: readonly ListedMachine[]): Promise<DeviceCredentials | null> {
    let credentials = await readCredentials();
    if (!credentials) return null;

    const next: Record<string, string> = {};
    for (const machine of machines) {
      const reason = unreachableReason(machine, window.location.protocol);
      if (reason) next[machine.id] = reason;
    }

    for (const machine of machinesNeedingGrants(machines, credentials, window.location.protocol)) {
      try {
        const grant = await requestGrant(machine.id, credentials.token);
        credentials = (await saveGrant(grant)) ?? credentials;
      } catch (error) {
        next[machine.id] = error instanceof Error ? error.message : String(error);
      }
    }

    problems.value = next;
    return credentials;
  }

  /**
   * A machine turned the phone's token away (401): drop it and ask home for a new one, once per machine per page
   * load. Returns the new token, or null when there's none to be had.
   */
  async function renewGrant(machineId: string): Promise<string | null> {
    if (retried.has(machineId)) return null;
    retried.add(machineId);
    const credentials = await removeGrant(machineId);
    if (!credentials) return null;
    try {
      const grant = await requestGrant(machineId, credentials.token);
      await saveGrant(grant);
      return grant.token;
    } catch (error) {
      problems.value = { ...problems.value, [machineId]: error instanceof Error ? error.message : String(error) };
      return null;
    }
  }

  return { problems, ensureGrants, renewGrant };
}
