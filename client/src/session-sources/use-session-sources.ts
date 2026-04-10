import { useEffect, useMemo, useState } from "react";
import { apiFetch } from "@/lib/api-client";
import type { SessionSourceCatalogResponse } from "@/lib/api-types";
import { getSessionSourceContributions } from "@/plugins/slots";
import { usePluginRuntime } from "@/plugins/context";
import { mergeSessionSources } from "./registry";

export function useSessionSources() {
  const { manifests } = usePluginRuntime();
  const [catalog, setCatalog] = useState<SessionSourceCatalogResponse | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    async function loadCatalog() {
      setIsLoading(true);
      setError(null);

      try {
        const response = await apiFetch("/api/session-sources/catalog");
        if (!response.ok) {
          const data = await response.json().catch(() => ({}));
          throw new Error((data as { error?: string }).error ?? "Failed to load session sources");
        }

        const data = (await response.json()) as SessionSourceCatalogResponse;
        if (!cancelled) {
          setCatalog(data);
        }
      } catch (err: unknown) {
        if (!cancelled) {
          setError(err instanceof Error ? err.message : "Unknown error");
        }
      } finally {
        if (!cancelled) {
          setIsLoading(false);
        }
      }
    }

    void loadCatalog();

    return () => {
      cancelled = true;
    };
  }, []);

  const sources = useMemo(() => {
    const backendSources = catalog?.sources ?? [];
    const contributions = getSessionSourceContributions(manifests).map((contribution) => ({
      pluginId: contribution.pluginId,
      id: contribution.id,
      sourceKey: contribution.sourceKey,
      label: contribution.label,
      description: contribution.description,
      icon: contribution.icon,
      order: contribution.order,
      formComponent: contribution.formComponent,
    }));

    return mergeSessionSources(backendSources, contributions);
  }, [catalog, manifests]);

  return {
    sources,
    isLoading,
    error,
  };
}
