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
  /** `machine` for another Fleet paired with a code; a phone or browser is `device`. Missing from an older Fleet. */
  kind?: "device" | "machine";
  /** Your Fleet: the machine it connected through (its home), by name. Set only on a row of the Fleet-wide list. */
  throughName?: string | null;
  /** Your Fleet: this row is one phone or browser of the whole Fleet, so Remove reaches every machine by its Fleet-wide id. */
  fleetWide?: boolean;
}

/** A machine that didn't answer when the Fleet-wide list was asked for. */
export interface NotAnsweringMachine {
  id: string;
  name: string;
}

interface FleetScreenDto {
  id: string;
  name: string;
  platform: string | null;
  throughId: string | null;
  throughName: string | null;
  createdAt: string;
  lastUsedAt: string;
  machines: string[];
}

/** A new one-time pairing code. */
export interface PairingCodeResponse {
  secret: string;
  manualCode: string;
  expiresAt: string;
  /** What's left by the machine's own clock; absent from an older machine. */
  expiresInSeconds?: number;
  url: string;
  payload: PairingPayloadV1;
}

/**
 * When a code runs out by this screen's clock. The machine that made it counts in its own time, which can differ from
 * the screen's by minutes; `expiresInSeconds` doesn't depend on either clock, so it wins where the machine says it.
 */
export function codeDeadline(code: { expiresAt: string; expiresInSeconds?: number }, receivedAt: number): number {
  return typeof code.expiresInSeconds === "number" ? receivedAt + code.expiresInSeconds * 1000 : Date.parse(code.expiresAt);
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

/**
 * Your Fleet (Labs): every phone and browser in the Fleet, each once, with the machine it connected through. Machines that
 * don't answer are named, and the rest still list.
 */
export async function listFleetScreens(): Promise<{ screens: PairedDevice[]; notAnswering: NotAnsweringMachine[] }> {
  const response = await fetchOnMachine(null, "/api/machine/screens");
  if (!response.ok) throw new Error(await readError(response, "Couldn't list the phones and browsers."));
  const body = await response.json() as { screens: FleetScreenDto[]; notAnswering: NotAnsweringMachine[] };
  return {
    screens: body.screens.map((screen) => ({
      id: screen.id,
      name: screen.name,
      platform: screen.platform,
      createdAt: screen.createdAt,
      lastUsedAt: screen.lastUsedAt,
      pairedVia: screen.throughId,
      kind: "device" as const,
      throughName: screen.throughName,
      fleetWide: true,
    })),
    notAnswering: body.notAnswering,
  };
}

/** Removes a phone or browser from every machine by its Fleet-wide id (a phone's device id, or `screen:{id}`). */
export async function removeFleetScreen(id: string): Promise<void> {
  const response = await fetchOnMachine(null, `/api/machine/screens/${encodeURIComponent(id)}`, { method: "DELETE" });
  if (!response.ok && response.status !== 404) throw new Error(await readError(response, "Couldn't remove it."));
}

export async function removeDevice(id: string): Promise<void> {
  const response = await fetchOnMachine(null, `/api/machine/devices/${encodeURIComponent(id)}`, { method: "DELETE" });
  if (!response.ok && response.status !== 404) throw new Error(await readError(response, "Couldn't remove the device."));
}

/** Fleet listens on this computer only and has no address of its own to put in a code. */
export class NoPhoneAddressError extends Error {}

/**
 * Asks for a one-time code. It carries every address Fleet can be reached on, so a phone tries each and nobody picks
 * one. `ownAddress` is one of the person's own (a tunnel, a domain), which goes first.
 */
export async function createPairingCode(ownAddress?: string): Promise<PairingCodeResponse> {
  const response = await fetchOnMachine(null, "/api/machine/pairing", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(ownAddress ? { baseUrl: ownAddress } : {}),
  });
  if (!response.ok) {
    const message = await readError(response, "Couldn't make a pairing code.");
    throw ownAddress || response.status !== 400 ? new Error(message) : new NoPhoneAddressError(message);
  }
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
