// Pairing with a Fleet: reading what was scanned or typed, and the two calls that trade a one-time code for a device key.
import { decodePairingFragment, normalizeManualCode } from "@fleet/lib/pairing";
import type { Machine } from "~/fleet/machines-core";

export class PairingError extends Error {}

export const HTTPS_ONLY = "Fleet's address must start with https://. On the computer, Settings → Machines → Add a phone shows how.";

export interface PairingTarget {
  baseUrl: string;
  secret?: string;
  manualCode?: string;
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
  return { baseUrl: checkAddress(payload.url), secret: payload.secret };
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

export async function previewPairing(target: PairingTarget): Promise<{ machineId: string; machineName: string }> {
  return (await post(target, "/api/pairing/preview", {})) as { machineId: string; machineName: string };
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
    token: body.token,
    deviceId: body.deviceId,
    pairedAt: Date.now(),
  };
}
