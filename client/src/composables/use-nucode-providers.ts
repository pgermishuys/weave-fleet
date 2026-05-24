import { readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";
import type {
  NuCodeProvider,
  NuCodeStoreCredentialsRequest,
  NuCodeTestConnectionResponse,
  NuCodeDeviceCodeResponse,
  NuCodeDevicePollResponse,
} from "@/lib/api-types";

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
      const response = await apiFetch("/api/nucode/providers");
      if (!response.ok) {
        const data = (await response.json().catch(() => ({}))) as { error?: string };
        throw new Error(data.error ?? `HTTP ${response.status}`);
      }
      const data = (await response.json()) as unknown;
      if (!Array.isArray(data)) {
        throw new Error("Unexpected response shape from /api/nucode/providers");
      }
      providers.value = data as NuCodeProvider[];
    } catch (fetchError) {
      error.value = fetchError instanceof Error ? fetchError.message : "Failed to load providers";
    } finally {
      isLoading.value = false;
    }
  }

  async function storeCredentials(providerId: string, fields: Record<string, string>): Promise<void> {
    const request: NuCodeStoreCredentialsRequest = { fields };
    const response = await apiFetch(`/api/nucode/providers/${encodeURIComponent(providerId)}/credentials`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request),
    });

    if (!response.ok) {
      const data = (await response.json().catch(() => ({}))) as { error?: string };
      throw new Error(data.error ?? `HTTP ${response.status}`);
    }

    await fetchProviders();
  }

  async function deleteCredentials(providerId: string): Promise<void> {
    const response = await apiFetch(`/api/nucode/providers/${encodeURIComponent(providerId)}/credentials`, {
      method: "DELETE",
    });

    if (!response.ok) {
      const data = (await response.json().catch(() => ({}))) as { error?: string };
      throw new Error(data.error ?? `HTTP ${response.status}`);
    }

    await fetchProviders();
  }

  async function testConnection(providerId: string): Promise<NuCodeTestConnectionResponse> {
    const response = await apiFetch(`/api/nucode/providers/${encodeURIComponent(providerId)}/test`, {
      method: "POST",
    });

    if (!response.ok) {
      const data = (await response.json().catch(() => ({}))) as { error?: string };
      throw new Error(data.error ?? `HTTP ${response.status}`);
    }

    return (await response.json()) as NuCodeTestConnectionResponse;
  }

  async function requestDeviceCode(providerId: string): Promise<NuCodeDeviceCodeResponse> {
    const response = await apiFetch(`/api/nucode/providers/${encodeURIComponent(providerId)}/auth/device-code`, {
      method: "POST",
    });

    if (!response.ok) {
      const data = (await response.json().catch(() => ({}))) as { error?: string };
      throw new Error(data.error ?? `HTTP ${response.status}`);
    }

    return (await response.json()) as NuCodeDeviceCodeResponse;
  }

  async function pollDeviceFlow(providerId: string, deviceCode: string): Promise<NuCodeDevicePollResponse> {
    const response = await apiFetch(`/api/nucode/providers/${encodeURIComponent(providerId)}/auth/poll`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ deviceCode }),
    });

    if (!response.ok) {
      const data = (await response.json().catch(() => ({}))) as { error?: string };
      throw new Error(data.error ?? `HTTP ${response.status}`);
    }

    return (await response.json()) as NuCodeDevicePollResponse;
  }

  return {
    providers: readonly(providers),
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
