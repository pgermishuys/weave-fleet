
import { useState, useEffect, useCallback } from "react";
import { apiFetch } from "@/lib/api-client";
import type { CredentialSummary, StoreCredentialRequest } from "@/lib/api-types";

export interface UseCredentialsResult {
  credentials: CredentialSummary[];
  isLoading: boolean;
  error?: string;
  refresh: () => void;
  storeCredential: (req: StoreCredentialRequest) => Promise<void>;
  updateCredential: (id: string, req: StoreCredentialRequest) => Promise<void>;
  deleteCredential: (id: string) => Promise<void>;
}

export function useCredentials(): UseCredentialsResult {
  const [credentials, setCredentials] = useState<CredentialSummary[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | undefined>();

  const fetchCredentials = useCallback(async () => {
    setIsLoading(true);
    setError(undefined);
    try {
      const response = await apiFetch("/api/credentials");
      if (!response.ok) {
        const data = await response.json().catch(() => ({}));
        throw new Error((data as { error?: string }).error ?? `HTTP ${response.status}`);
      }
      const data = (await response.json()) as CredentialSummary[];
      setCredentials(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load API keys");
    } finally {
      setIsLoading(false);
    }
  }, []);

  useEffect(() => {
    void fetchCredentials();
  }, [fetchCredentials]);

  const storeCredential = useCallback(async (req: StoreCredentialRequest) => {
    const response = await apiFetch("/api/credentials", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(req),
    });
    if (!response.ok) {
      const data = (await response.json().catch(() => ({}))) as { error?: string };
      throw new Error(data.error ?? `HTTP ${response.status}`);
    }
    await fetchCredentials();
  }, [fetchCredentials]);

  const updateCredential = useCallback(async (id: string, req: StoreCredentialRequest) => {
    const response = await apiFetch(`/api/credentials/${encodeURIComponent(id)}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(req),
    });
    if (!response.ok) {
      const data = (await response.json().catch(() => ({}))) as { error?: string };
      throw new Error(data.error ?? `HTTP ${response.status}`);
    }
    await fetchCredentials();
  }, [fetchCredentials]);

  const deleteCredential = useCallback(async (id: string) => {
    const response = await apiFetch(`/api/credentials/${encodeURIComponent(id)}`, {
      method: "DELETE",
    });
    if (!response.ok) {
      const data = (await response.json().catch(() => ({}))) as { error?: string };
      throw new Error(data.error ?? `HTTP ${response.status}`);
    }
    await fetchCredentials();
  }, [fetchCredentials]);

  return {
    credentials,
    isLoading,
    error,
    refresh: fetchCredentials,
    storeCredential,
    updateCredential,
    deleteCredential,
  };
}
