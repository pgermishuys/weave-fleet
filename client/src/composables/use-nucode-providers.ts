import { readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { api } from "@/api/client";
import type {
  NuCodeProvider,
  NuCodeStoreCredentialsRequest,
  NuCodeTestConnectionResponse,
  NuCodeDeviceCodeResponse,
  NuCodeDevicePollResponse,
} from "@/api/client";

export interface UseNuCodeProvidersResult {
  providers: Readonly<Ref<readonly NuCodeProvider[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  fetchProviders: () => Promise<void>;
  storeCredentials: (providerId: string, fields: Record<string, string>) => Promise<void>;
  deleteCredentials: (providerId: string) => Promise<void>;
  testConnection: (providerId: string) => Promise<NuCodeTestConnectionResponse>;
  requestDeviceCode: (providerId: string) => Promise<NuCodeDeviceCodeResponse>;
  pollDeviceFlow: (providerId: string, deviceCode: string) => Promise<NuCodeDevicePollResponse>;
}

export function useNuCodeProviders(): UseNuCodeProvidersResult {
  const providers = ref<NuCodeProvider[]>([]);
  const isLoading = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);

  async function fetchProviders(): Promise<void> {
    isLoading.value = true;
    error.value = undefined;

    try {
      const { data, error: apiError } = await api.GET("/api/nucode/providers");
      if (apiError) {
        throw new Error(apiError ? String(apiError) : "Failed to load providers");
      }
      if (!data) {
        throw new Error("No data returned");
      }
      const result = data as unknown;
      if (!Array.isArray(result)) {
        throw new Error("Unexpected response shape from /api/nucode/providers");
      }
      providers.value = result as NuCodeProvider[];
    } catch (fetchError) {
      error.value = fetchError instanceof Error ? fetchError.message : "Failed to load providers";
    } finally {
      isLoading.value = false;
    }
  }

  async function storeCredentials(providerId: string, fields: Record<string, string>): Promise<void> {
    const request: NuCodeStoreCredentialsRequest = { fields };
    const { error: apiError } = await api.PUT("/api/nucode/providers/{id}/credentials", {
      params: { path: { id: providerId } },
      body: request as never,
    });

    if (apiError) {
      throw new Error(apiError ? String(apiError) : "Failed to store credentials");
    }

    await fetchProviders();
  }

  async function deleteCredentials(providerId: string): Promise<void> {
    const { error: apiError } = await api.DELETE("/api/nucode/providers/{id}/credentials", {
      params: { path: { id: providerId } },
    });

    if (apiError) {
      throw new Error(apiError ? String(apiError) : "Failed to delete credentials");
    }

    await fetchProviders();
  }

  async function testConnection(providerId: string): Promise<NuCodeTestConnectionResponse> {
    const { data, error: apiError } = await api.POST("/api/nucode/providers/{id}/test", {
      params: { path: { id: providerId } },
    });

    if (apiError) {
      throw new Error(apiError ? String(apiError) : "Failed to test connection");
    }

    if (!data) {
      throw new Error("No data returned");
    }

    return data as NuCodeTestConnectionResponse;
  }

  async function requestDeviceCode(providerId: string): Promise<NuCodeDeviceCodeResponse> {
    const { data, error: apiError } = await api.POST("/api/nucode/providers/{id}/auth/device-code", {
      params: { path: { id: providerId } },
    });

    if (apiError) {
      throw new Error(apiError ? String(apiError) : "Failed to request device code");
    }

    if (!data) {
      throw new Error("No data returned");
    }

    return data as NuCodeDeviceCodeResponse;
  }

  async function pollDeviceFlow(providerId: string, deviceCode: string): Promise<NuCodeDevicePollResponse> {
    const { data, error: apiError } = await api.POST("/api/nucode/providers/{id}/auth/poll", {
      params: { path: { id: providerId } },
      body: { deviceCode } as never,
    });

    if (apiError) {
      throw new Error(apiError ? String(apiError) : "Failed to poll device flow");
    }

    if (!data) {
      throw new Error("No data returned");
    }

    return data as NuCodeDevicePollResponse;
  }

  return {
    providers: readonly(providers) as Readonly<Ref<readonly NuCodeProvider[]>>,
    isLoading: readonly(isLoading),
    error: readonly(error),
    fetchProviders,
    storeCredentials,
    deleteCredentials,
    testConnection,
    requestDeviceCode,
    pollDeviceFlow,
  };
}
