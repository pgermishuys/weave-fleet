import { shallowRef } from "vue";
import { readCredentials, removeGrant, saveGrant, type DeviceCredentials, type MachineGrant } from "@/lib/device-credentials";
import { machinesNeedingGrants, requestGrant, unreachableReason, type ListedMachine } from "@/lib/phone/grants";
import { removeScreenGrant, saveScreenGrant, screenCredentials, screenId, screenName } from "@/lib/phone/screen-keys";
import { YOUR_FLEET_PREFERENCE_KEY } from "@/lib/your-fleet";

/**
 * With Your Fleet on (Labs), a screen that isn't a paired phone (a browser signed in on the machine) holds keys the
 * same way: home mints them for the screen's id. `null` when the screen isn't in that mode.
 */
async function screenHome(): Promise<{ id: string; name: string } | null> {
  try {
    const preferences = await fetch("/api/preferences", { credentials: "include" });
    if (!preferences.ok) return null;
    const values = await preferences.json() as Record<string, string>;
    if (values[YOUR_FLEET_PREFERENCE_KEY] !== "true") return null;
    const machine = await fetch("/api/machine", { credentials: "include" });
    if (!machine.ok) return null;
    const body = await machine.json() as { id?: string; name?: string };
    return body.id ? { id: body.id, name: body.name ?? "This machine" } : null;
  } catch {
    return null;
  }
}

/**
 * The phone's keys to other machines. After pairing and whenever the inbox loads, the phone asks home for its own
 * token on every machine in home's list it lacks one for; a machine that turns a token away gets one fresh request.
 * Machines the phone can't reach directly (http on an https page) are skipped with a reason. A browser that isn't a
 * paired phone does the same under Your Fleet (Labs), with its own id in place of a device token.
 */
export function useMachineGrants() {
  const problems = shallowRef<Record<string, string>>({});
  const retried = new Set<string>();
  let isScreen = false;

  async function credentialsNow(): Promise<DeviceCredentials | null> {
    const phone = await readCredentials();
    if (phone) {
      isScreen = false;
      return phone;
    }
    const home = await screenHome();
    isScreen = home !== null;
    return home ? screenCredentials(home) : null;
  }

  function ask(machineId: string, credentials: DeviceCredentials): Promise<MachineGrant> {
    return isScreen
      ? requestGrant(machineId, null, { screenId: screenId(), name: screenName() })
      : requestGrant(machineId, credentials.token);
  }

  async function keep(grant: MachineGrant): Promise<DeviceCredentials | null> {
    if (!isScreen) return await saveGrant(grant);
    saveScreenGrant(grant);
    return await credentialsNow();
  }

  async function ensureGrants(machines: readonly ListedMachine[]): Promise<DeviceCredentials | null> {
    let credentials = await credentialsNow();
    if (!credentials) return null;

    const next: Record<string, string> = {};
    for (const machine of machines) {
      const reason = unreachableReason(machine, window.location.protocol);
      if (reason) next[machine.id] = reason;
    }

    for (const machine of machinesNeedingGrants(machines, credentials, window.location.protocol)) {
      try {
        const grant = await ask(machine.id, credentials);
        credentials = (await keep(grant)) ?? credentials;
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
    const credentials = await credentialsNow();
    if (!credentials) return null;
    if (isScreen) removeScreenGrant(machineId);
    else await removeGrant(machineId);
    try {
      const grant = await ask(machineId, credentials);
      await keep(grant);
      return grant.token;
    } catch (error) {
      problems.value = { ...problems.value, [machineId]: error instanceof Error ? error.message : String(error) };
      return null;
    }
  }

  return { problems, ensureGrants, renewGrant };
}
