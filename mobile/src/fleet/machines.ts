// The machines store on a phone: Keychain (iOS) / Keystore-backed storage (Android), localStorage on web.
import * as SecureStore from "expo-secure-store";
import { useSyncExternalStore } from "react";
import { Platform } from "react-native";
import { MachinesStore, type KeyValueStorage } from "~/fleet/machines-core";

const web = Platform.OS === "web";
export const storage: KeyValueStorage = {
  get: async (key) => (web ? globalThis.localStorage?.getItem(key) ?? null : SecureStore.getItemAsync(key)),
  set: async (key, value) => {
    if (web) globalThis.localStorage?.setItem(key, value);
    else await SecureStore.setItemAsync(key, value, { keychainAccessible: SecureStore.AFTER_FIRST_UNLOCK });
  },
  remove: async (key) => {
    if (web) globalThis.localStorage?.removeItem(key);
    else await SecureStore.deleteItemAsync(key);
  },
};

export const machines = new MachinesStore(storage);

export function useMachines() {
  return useSyncExternalStore(machines.subscribe, () => machines.all());
}
