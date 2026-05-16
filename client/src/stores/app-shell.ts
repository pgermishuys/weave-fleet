import type { ClientConfigResponse, UserMeResponse } from "@/lib/api-types";
import { defineStore } from "pinia";
import { computed, shallowRef } from "vue";

export interface AppShellConfig extends ClientConfigResponse {
  appName: string;
  profile: string;
}

export interface AppShellUser extends UserMeResponse {
  id: string;
  name: string;
}

function createDefaultConfig(): AppShellConfig {
  return {
    appName: "Weave Fleet",
    profile: "default",
    cloudMode: false,
    authEnabled: false,
    tokenAuthEnabled: false,
    availableHarnesses: [],
  };
}

function mapUser(user: UserMeResponse): AppShellUser {
  return {
    ...user,
    id: user.userId,
    name: user.displayName?.trim() || user.email?.trim() || "User",
  };
}

export const useAppShellStore = defineStore("app-shell", () => {
  const config = shallowRef<AppShellConfig>(createDefaultConfig());
  const user = shallowRef<AppShellUser | null>(null);
  const isLoading = shallowRef(false);
  const isAuthenticated = computed(() => user.value !== null);

  function setConfig(value: ClientConfigResponse): void {
    config.value = {
      ...config.value,
      ...value,
    };
  }

  function setUser(value: UserMeResponse): void {
    user.value = mapUser(value);
  }

  function setShellData(clientConfig: ClientConfigResponse, currentUser: UserMeResponse): void {
    setConfig(clientConfig);
    setUser(currentUser);
  }

  function setLoading(value: boolean): void {
    isLoading.value = value;
  }

  function clear(): void {
    config.value = createDefaultConfig();
    user.value = null;
    isLoading.value = false;
  }

  return {
    config,
    user,
    isLoading,
    isAuthenticated,
    setConfig,
    setUser,
    setShellData,
    setLoading,
    clear,
  };
});
