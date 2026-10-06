/**
 * The home machine's paired devices and pairing codes (owner only; a paired device gets 403). See
 * docs/machines.md. Always the home machine: devices are paired with the Fleet that serves the page.
 */
import { fetchOnMachine } from "@/lib/machines";
import type { PairingPayloadV1 } from "@/lib/pairing";

/** A device with its own token, as `GET /api/machine/devices` lists it. */
export interface PairedDevice {
  id: string;
  name: string;
  platform: string | null;
  createdAt: string;
  lastUsedAt: string;
  /** The machine that asked for this device's token on its behalf; null when it paired here. */
  pairedVia: string | null;
}

/** A new one-time pairing code. */
export interface PairingCodeResponse {
  secret: string;
  manualCode: string;
  expiresAt: string;
  url: string;
  payload: PairingPayloadV1;
}

async function readError(response: Response, fallback: string): Promise<string> {
  try {
    const body = await response.json() as { error?: string; message?: string };
    return body.error ?? body.message ?? fallback;
  } catch {
    return fallback;
  }
}

export async function listDevices(): Promise<PairedDevice[]> {
  const response = await fetchOnMachine(null, "/api/machine/devices");
  if (!response.ok) throw new Error(await readError(response, "Couldn't list the devices."));
  return ((await response.json()) as { devices: PairedDevice[] }).devices;
}

export async function removeDevice(id: string): Promise<void> {
  const response = await fetchOnMachine(null, `/api/machine/devices/${encodeURIComponent(id)}`, { method: "DELETE" });
  if (!response.ok && response.status !== 404) throw new Error(await readError(response, "Couldn't remove the device."));
}

/** Asks for a one-time code a phone opening `baseUrl` can redeem. */
export async function createPairingCode(baseUrl: string): Promise<PairingCodeResponse> {
  const response = await fetchOnMachine(null, "/api/machine/pairing", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ baseUrl }),
  });
  if (!response.ok) throw new Error(await readError(response, "Couldn't make a pairing code."));
  return await response.json() as PairingCodeResponse;
}

/** Saves the address phones should use for the home machine; empty clears it. */
export async function savePublicUrl(publicUrl: string): Promise<void> {
  const response = await fetchOnMachine(null, "/api/machine", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ publicUrl }),
  });
  if (!response.ok) throw new Error(await readError(response, "Couldn't save the phone address."));
}

/** A pairing code, by its QR secret or its typed form. */
export type PairingCodeRef = { secret: string } | { manualCode: string };

/** The machine a pairing code would connect to. */
export interface PairingPreview {
  machineId: string;
  machineName: string;
  os: string;
  expiresAt: string;
}

/** What redeeming a code returns: the phone's own token for this machine, and who the machine is. */
export interface PairingRedeemed {
  deviceId: string;
  token: string;
  machine: { id: string; name: string; os: string; publicUrl?: string | null };
}

/** Thrown when the code is unknown, used or expired (404). */
export class PairingCodeGoneError extends Error {}

async function postPairing<T>(path: string, body: unknown): Promise<T> {
  // Same origin, so the redeem's device cookie lands in this browser.
  const response = await fetch(path, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (response.status === 404) throw new PairingCodeGoneError(await readError(response, "This code has expired or was already used."));
  if (!response.ok) throw new Error(await readError(response, `Pairing failed (${response.status}).`));
  return await response.json() as T;
}

export function previewPairing(code: PairingCodeRef): Promise<PairingPreview> {
  return postPairing("/api/pairing/preview", code);
}

export function redeemPairing(code: PairingCodeRef, deviceName: string, platform: string): Promise<PairingRedeemed> {
  return postPairing("/api/pairing/redeem", { ...code, deviceName, platform });
}
