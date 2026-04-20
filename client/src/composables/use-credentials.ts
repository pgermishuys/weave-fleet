import { readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";
import type { CredentialSummary, StoreCredentialRequest } from "@/lib/api-types";

export interface UseCredentialsResult {
  credentials: Readonly<Ref<readonly CredentialSummary[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  refresh: () => Promise<void>;
  storeCredential: (request: StoreCredentialRequest) => Promise<void>;
  updateCredential: (id: string, request: StoreCredentialRequest) => Promise<void>;
  deleteCredential: (id: string) => Promise<void>;
}

export function useCredentials(): UseCredentialsResult {
  const credentials = ref<CredentialSummary[]>([]);
  const isLoading = shallowRef(true);
  const error = shallowRef<string | undefined>(undefined);

  async function fetchCredentials(): Promise<void> {
    isLoading.value = true;
    error.value = undefined;

    try {
      const response = await apiFetch("/api/credentials");
      if (!response.ok) {
        const data = (await response.json().catch(() => ({}))) as { error?: string };
        throw new Error(data.error ?? `HTTP ${response.status}`);
      }

      credentials.value = (await response.json()) as CredentialSummary[];
    } catch (fetchError) {
      error.value = fetchError instanceof Error ? fetchError.message : "Failed to load API keys";
    } finally {
      isLoading.value = false;
    }
  }

  async function storeCredential(request: StoreCredentialRequest): Promise<void> {
    const response = await apiFetch("/api/credentials", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request),
    });

    if (!response.ok) {
      const data = (await response.json().catch(() => ({}))) as { error?: string };
      throw new Error(data.error ?? `HTTP ${response.status}`);
    }

    await fetchCredentials();
  }

  async function updateCredential(id: string, request: StoreCredentialRequest): Promise<void> {
    const response = await apiFetch(`/api/credentials/${encodeURIComponent(id)}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request),
    });

    if (!response.ok) {
      const data = (await response.json().catch(() => ({}))) as { error?: string };
      throw new Error(data.error ?? `HTTP ${response.status}`);
    }

    await fetchCredentials();
  }

  async function deleteCredential(id: string): Promise<void> {
    const response = await apiFetch(`/api/credentials/${encodeURIComponent(id)}`, {
      method: "DELETE",
    });

    if (!response.ok) {
      const data = (await response.json().catch(() => ({}))) as { error?: string };
      throw new Error(data.error ?? `HTTP ${response.status}`);
    }

    await fetchCredentials();
  }

  void fetchCredentials();

  return {
    credentials: readonly(credentials),
    isLoading: readonly(isLoading),
    error: readonly(error),
    refresh: fetchCredentials,
    storeCredential,
    updateCredential,
    deleteCredential,
  };
}
