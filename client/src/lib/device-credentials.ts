/**
 * The phone's own keys: the device token its home machine gave it when it paired, and the tokens other machines
 * gave it through home (device grants). See docs/phone.md.
 *
 * Kept in IndexedDB, which the service worker can read (to answer a notification's Allow once), with a copy in
 * localStorage so the page can read them synchronously at startup. No Vue and no `window` beyond the guarded
 * localStorage mirror: the service worker build imports this module too.
 */

/** A token another machine gave this phone, through its home machine. */
export interface MachineGrant {
  machineId: string;
  /** Where the phone reaches that machine, e.g. `https://falcon.tail9c2e.ts.net`. */
  baseUrl: string;
  token: string;
}

export interface DeviceCredentials {
  /** The machine the phone paired with: it sends the phone's notifications for every machine. */
  homeMachineId: string;
  homeMachineName: string;
  /** Home's base URL as the phone opened it; the page's own origin when the app runs there. */
  homeBaseUrl: string;
  deviceId: string;
  /** The device token for home. */
  token: string;
  grants: MachineGrant[];
  pairedAt: string;
}

const DB_NAME = "weave-fleet-device";
const STORE = "credentials";
const KEY = "current";
const MIRROR_KEY = "weave:device-credentials";

function storage(): Storage | null {
  try {
    return typeof localStorage === "undefined" ? null : localStorage;
  } catch {
    return null;
  }
}

function isCredentials(value: unknown): value is DeviceCredentials {
  if (!value || typeof value !== "object") return false;
  const candidate = value as Partial<DeviceCredentials>;
  return typeof candidate.homeMachineId === "string"
    && typeof candidate.deviceId === "string"
    && typeof candidate.token === "string"
    && Array.isArray(candidate.grants);
}

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, 1);
    request.onupgradeneeded = () => request.result.createObjectStore(STORE);
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error ?? new Error("Couldn't open the device store."));
  });
}

async function withStore<T>(mode: IDBTransactionMode, run: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  const db = await openDb();
  try {
    return await new Promise<T>((resolve, reject) => {
      const transaction = db.transaction(STORE, mode);
      const request = run(transaction.objectStore(STORE));
      transaction.oncomplete = () => resolve(request.result);
      transaction.onerror = () => reject(transaction.error ?? new Error("The device store failed."));
      transaction.onabort = () => reject(transaction.error ?? new Error("The device store failed."));
    });
  } finally {
    db.close();
  }
}

/** The credentials as the page last saved them, read synchronously from the localStorage copy. */
export function readCredentialsSync(): DeviceCredentials | null {
  try {
    const raw = storage()?.getItem(MIRROR_KEY);
    const parsed: unknown = raw ? JSON.parse(raw) : null;
    return isCredentials(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

/** The credentials from IndexedDB (what the service worker sees), else the localStorage copy. */
export async function readCredentials(): Promise<DeviceCredentials | null> {
  if (typeof indexedDB !== "undefined") {
    try {
      const stored = await withStore("readonly", (store) => store.get(KEY) as IDBRequest<unknown>);
      if (isCredentials(stored)) return stored;
    } catch {
      // Private browsing and some embedded views have no IndexedDB; the mirror still works for the page.
    }
  }
  return readCredentialsSync();
}

export async function saveCredentials(credentials: DeviceCredentials): Promise<void> {
  storage()?.setItem(MIRROR_KEY, JSON.stringify(credentials));
  if (typeof indexedDB === "undefined") return;
  try {
    await withStore("readwrite", (store) => store.put(credentials, KEY));
  } catch {
    // The mirror holds them; only answering from a notification needs IndexedDB.
  }
}

export async function clearCredentials(): Promise<void> {
  storage()?.removeItem(MIRROR_KEY);
  if (typeof indexedDB === "undefined") return;
  try {
    await withStore("readwrite", (store) => store.delete(KEY));
  } catch {
    // Nothing to clear.
  }
}

/** Adds or replaces the grant for `grant.machineId`. No-op when the phone has no credentials. */
export async function saveGrant(grant: MachineGrant): Promise<DeviceCredentials | null> {
  const current = await readCredentials();
  if (!current) return null;
  const next = { ...current, grants: [...current.grants.filter((g) => g.machineId !== grant.machineId), grant] };
  await saveCredentials(next);
  return next;
}

export async function removeGrant(machineId: string): Promise<DeviceCredentials | null> {
  const current = await readCredentials();
  if (!current) return null;
  const next = { ...current, grants: current.grants.filter((g) => g.machineId !== machineId) };
  await saveCredentials(next);
  return next;
}

/** Where and with what token the phone reaches `machineId`; null when it has no key for it. */
export function credentialFor(credentials: DeviceCredentials, machineId: string): { baseUrl: string; token: string } | null {
  if (machineId === credentials.homeMachineId) return { baseUrl: credentials.homeBaseUrl, token: credentials.token };
  const grant = credentials.grants.find((candidate) => candidate.machineId === machineId);
  return grant ? { baseUrl: grant.baseUrl, token: grant.token } : null;
}
