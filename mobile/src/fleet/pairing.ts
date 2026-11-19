// Pairing with a Fleet: reading what was scanned or typed, and the two calls that trade a one-time code for a device key.
import { decodePairingFragment, normalizeManualCode, pairingAddresses } from "@fleet/lib/pairing";
import type { Machine } from "~/fleet/machines-core";
import { canUse, kindOf, probeFor, raceAddresses, seedAddresses, type Probe } from "~/fleet/routes-core";

export class PairingError extends Error {}

export const HTTPS_ONLY = "Fleet's address must start with https://. In Fleet on the machine, Settings → Machines → Add your phone shows how.";

export interface PairingTarget {
  baseUrl: string;
  secret?: string;
  manualCode?: string;
  /** What a scanned code says about the machine, so each address can be checked against its id before the secret goes there. */
  machineId?: string;
  /** Every address the phone may use, best first. More than one means the code carried several; `baseUrl` is the first until one answers. */
  addresses?: string[];
}

/** https only; plain http is for this computer (the walk-through build talks to a Fleet on localhost). */
export function checkAddress(text: string): string {
  const typed = text.trim().replace(/\/+$/, "");
  const match = /^(https?):\/\/([^/:?#\s]+|\[[^\]]+\])(:\d+)?(\/[^?#\s]*)?$/i.exec(typed);
  if (!match) throw new PairingError(HTTPS_ONLY);
  const loopback = /^(localhost|127\.0\.0\.1)$/i.test(match[2]);
  if (match[1].toLowerCase() === "http" && !loopback) throw new PairingError(HTTPS_ONLY);
  return typed;
}

/** What a scanned QR code means: the pairing link the computer shows, else null. */
export function targetFromScan(text: string): PairingTarget | null {
  const hash = text.trim().split("#")[1];
  const payload = hash ? decodePairingFragment(`#${hash}`) : null;
  if (!payload) return null;
  // Every address in the code that this phone may use. Plain http is fine on Tailscale and the home network, once the
  // machine's id has answered there (`resolvePairing`); a single address from an older code keeps the old https rule.
  const all = pairingAddresses(payload);
  if (all.length === 1) return { baseUrl: checkAddress(all[0]), secret: payload.secret };
  const usable = all.filter((url) => canUse(url));
  if (usable.length === 0) throw new PairingError(HTTPS_ONLY);
  return { baseUrl: usable[0], secret: payload.secret, machineId: payload.machineId, addresses: usable };
}

/** What typing an address and a code means. */
export function targetFromTyped(address: string, code: string): PairingTarget {
  const baseUrl = checkAddress(address);
  const manualCode = normalizeManualCode(code);
  if (!manualCode) throw new PairingError("That code should be 8 letters and numbers, like K7QM-4T2X.");
  return { baseUrl, manualCode };
}

/** A fetch that gives up after 15 s, so a wrong address says so instead of spinning. */
async function post(target: PairingTarget, path: string, body: object): Promise<unknown> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 15_000);
  let response: Response;
  try {
    response = await fetch(`${target.baseUrl}${path}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      credentials: "omit",
      body: JSON.stringify({ secret: target.secret ?? null, manualCode: target.manualCode ?? null, ...body }),
      signal: controller.signal,
    });
  } catch {
    throw new PairingError(`Couldn't reach Fleet at ${target.baseUrl}.`);
  } finally {
    clearTimeout(timer);
  }
  if (!response.ok) {
    const failure = (await response.json().catch(() => null)) as { error?: string } | null;
    throw new PairingError(failure?.error ?? "That code didn't work.");
  }
  return response.json();
}

/**
 * The address to talk to: the first of the code's addresses where the machine answers with its own id. Nothing is sent
 * to an address that doesn't. A code with one address is used as it is.
 */
export async function resolvePairing(target: PairingTarget, probe: Probe | null = target.machineId ? probeFor(target.machineId) : null): Promise<PairingTarget> {
  const addresses = target.addresses ?? [];
  if (addresses.length < 2 || !probe) return target;
  const { winner } = await raceAddresses(addresses, probe);
  if (!winner) throw new PairingError(`Couldn't reach Fleet at ${addresses.length === 2 ? "either address" : "any of its addresses"} in this code. Is the phone on the same Wi-Fi, or on Tailscale?`);
  return { ...target, baseUrl: winner };
}

/** Which machine the code opens, and the address that answered for it; `target` is the one to redeem with. */
export async function previewPairing(target: PairingTarget): Promise<{ machineId: string; machineName: string; target: PairingTarget }> {
  const resolved = await resolvePairing(target);
  const answer = (await post(resolved, "/api/pairing/preview", {})) as { machineId: string; machineName: string };
  return { ...answer, target: resolved };
}

export async function redeemPairing(target: PairingTarget, deviceName: string, platform: string): Promise<Machine> {
  const body = (await post(target, "/api/pairing/redeem", { deviceName, platform: platform === "ios" || platform === "android" ? platform : "other" })) as {
    deviceId: string;
    token: string;
    machine: { id: string; name?: string | null };
  };
  return {
    machineId: body.machine.id,
    machineName: body.machine.name ?? "Fleet",
    baseUrl: target.baseUrl,
    // The other addresses in the code stay on the list; the machine reports its own once the phone is connected.
    addresses: [
      ...seedAddresses(target.baseUrl),
      ...(target.addresses ?? []).filter((url) => url.toLowerCase() !== target.baseUrl.toLowerCase()).map((url) => ({ url, kind: kindOf(url), learned: true })),
    ],
    token: body.token,
    deviceId: body.deviceId,
    pairedAt: Date.now(),
  };
}
