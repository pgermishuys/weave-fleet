// The phone's own device key for one Fleet, kept in the Keychain (iOS) or Keystore-backed storage (Android).
import * as SecureStore from "expo-secure-store";
import { Platform } from "react-native";

export interface Credentials {
  baseUrl: string;
  token: string;
  deviceId: string;
  machineId: string;
  machineName: string;
}

const KEY = "fleet.credentials.v1";
let cached: Credentials | null | undefined;

export async function loadCredentials(): Promise<Credentials | null> {
  if (cached !== undefined) return cached;
  const raw = Platform.OS === "web" ? globalThis.localStorage?.getItem(KEY) ?? null : await SecureStore.getItemAsync(KEY);
  cached = raw ? (JSON.parse(raw) as Credentials) : null;
  return cached;
}

export async function saveCredentials(credentials: Credentials | null): Promise<void> {
  cached = credentials;
  const raw = credentials ? JSON.stringify(credentials) : null;
  if (Platform.OS === "web") {
    if (raw) globalThis.localStorage?.setItem(KEY, raw);
    else globalThis.localStorage?.removeItem(KEY);
    return;
  }
  if (raw) await SecureStore.setItemAsync(KEY, raw, { keychainAccessible: SecureStore.AFTER_FIRST_UNLOCK });
  else await SecureStore.deleteItemAsync(KEY);
}

export function currentCredentials(): Credentials | null {
  return cached ?? null;
}
