/**
 * Rebuilding a signed-in phone's credentials. Adding Fleet to an iPhone's Home Screen copies Safari's cookies into the
 * app but none of its storage, so the app opens signed in (the device cookie) yet without the phone's token, and
 * without it can't get keys to the other machines. Fleet issues the phone a new token (the old one, left in Safari,
 * stops working) and says which machine this is. No Vue.
 */
import { saveCredentials, type DeviceCredentials } from "@/lib/device-credentials";

interface Reissued {
  deviceId: string;
  token: string;
  machine: { id: string; name: string };
}

/**
 * Asks this machine for a new token for the signed-in phone and saves the credentials. Null when the browser isn't
 * a paired phone (the owner's own browser gets a 400) or the request fails; the phone pages still work on the cookie.
 */
export async function restoreCredentials(
  origin: string = window.location.origin,
  fetchImpl: typeof fetch = globalThis.fetch.bind(globalThis),
): Promise<DeviceCredentials | null> {
  let reissued: Reissued;
  try {
    const response = await fetchImpl("/api/machine/devices/me/token", { method: "POST", credentials: "include" });
    if (!response.ok) return null;
    reissued = await response.json() as Reissued;
  } catch {
    return null;
  }
  if (!reissued?.token || !reissued.deviceId || !reissued.machine?.id) return null;

  const credentials: DeviceCredentials = {
    homeMachineId: reissued.machine.id,
    homeMachineName: reissued.machine.name,
    homeBaseUrl: origin,
    deviceId: reissued.deviceId,
    token: reissued.token,
    grants: [],
    pairedAt: new Date().toISOString(),
  };
  await saveCredentials(credentials);
  return credentials;
}
