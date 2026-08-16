import { readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { api } from "@/api/client";
import type { CredentialSummary, StoreCredentialRequest } from "@/api/client";
import { extractApiError } from "@/lib/api-error";

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
      const { data, error: apiError } = await api.GET("/api/credentials");
      if (apiError) {
        throw new Error(extractApiError(apiError, "Failed to load credentials"));
      }

      if (!data) {
        throw new Error("No data returned");
      }

      credentials.value = data as CredentialSummary[];
    } catch (fetchError) {
      error.value = fetchError instanceof Error ? fetchError.message : "Failed to load API keys";
    } finally {
      isLoading.value = false;
    }
  }

  async function storeCredential(request: StoreCredentialRequest): Promise<void> {
    const { error: apiError } = await api.PUT("/api/credentials", {
      body: request as never,
    });

    if (apiError) {
      throw new Error(extractApiError(apiError, "Failed to store credential"));
    }

    await fetchCredentials();
  }

  async function updateCredential(id: string, request: StoreCredentialRequest): Promise<void> {
    const { error: apiError } = await api.PUT("/api/credentials/{id}", {
      params: { path: { id } },
      body: request as never,
    });

    if (apiError) {
      throw new Error(extractApiError(apiError, "Failed to update credential"));
    }

    await fetchCredentials();
  }

  async function deleteCredential(id: string): Promise<void> {
    const { error: apiError } = await api.DELETE("/api/credentials/{id}", {
      params: { path: { id } },
    });

    if (apiError) {
      throw new Error(extractApiError(apiError, "Failed to delete credential"));
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
