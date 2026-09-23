import { storeToRefs } from "pinia";
import { computed, readonly, shallowRef, toValue, watch, type MaybeRefOrGetter } from "vue";
import { api } from "@/api/client";
import type { AvailableProvider } from "@/api/client";
import { sessionCatalogChanges } from "@/lib/harness-catalog-changes";
import { shareInFlight } from "@/lib/shared-request";
import { useSessionsStore } from "@/stores/sessions";

export interface ModelOption {
  id: string;
  name: string;
  providerId: string;
  selectionKey: string;
  provider: string;
  description: string;
  variants?: readonly string[];
}

export function createModelSelectionKey(providerId: string, modelId: string): string {
  return JSON.stringify([providerId, modelId]);
}

export function toModelOptions(providers: readonly AvailableProvider[]): ModelOption[] {
  return providers.flatMap((provider) => {
    return provider.models.map((model) => ({
      id: model.id,
      name: model.name,
      providerId: provider.id,
      selectionKey: createModelSelectionKey(provider.id, model.id),
      provider: provider.name,
      description: "",
      variants: model.variants,
    }));
  });
}

const loadSessionModels = shareInFlight(async (sessionId: string): Promise<ModelOption[]> => {
  const { data, error, response } = await api.GET("/api/sessions/{id}/models", {
    params: { path: { id: sessionId } },
  });

  if (error || !response.ok) {
    const payload = error as { error?: string } | undefined;
    throw new Error(payload?.error ?? `HTTP ${response.status}`);
  }

  const body = data as unknown as { providers?: AvailableProvider[] } | AvailableProvider[];
  return toModelOptions(Array.isArray(body) ? body : body.providers ?? []);
});

export function useModels(sessionId?: MaybeRefOrGetter<string | undefined>) {
  const sessionsStore = useSessionsStore();
  const { activeSessionId } = storeToRefs(sessionsStore);

  // Replaced whole, never changed in place. Shallow, because a deep proxy made every read of a model go through Vue:
  // naming the model on each of a hundred messages took a third of a second.
  const models = shallowRef<ModelOption[]>([]);
  const modelsByKey = shallowRef<Record<string, ModelOption>>({});
  const isLoading = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);

  const resolvedSessionId = computed(() => toValue(sessionId) ?? activeSessionId.value ?? "");
  const defaultModelKey = computed(() => models.value[0]?.selectionKey ?? "");
  // The harness says what the session's folder offers changed (a provider signed in, a model added): ask again.
  const changes = sessionCatalogChanges(resolvedSessionId);

  watch(
    [resolvedSessionId, changes],
    async ([nextSessionId], previous, onCleanup) => {
      if (!nextSessionId) {
        models.value = [];
        modelsByKey.value = {};
        isLoading.value = false;
        error.value = undefined;
        return;
      }

      // The request is shared with every other caller, so leaving only drops its answer.
      let left = false;
      onCleanup(() => {
        left = true;
      });

      // Asked again because the harness's list changed: the old one stays up meanwhile.
      const isRefresh = previous?.[0] === nextSessionId;
      if (!isRefresh) {
        isLoading.value = true;
      }
      error.value = undefined;

      try {
        const nextModels = await loadSessionModels(nextSessionId);
        if (left) {
          return;
        }

        models.value = nextModels;
        modelsByKey.value = Object.fromEntries(nextModels.map((model) => [model.selectionKey, model])) as Record<string, ModelOption>;
      } catch (fetchError) {
        if (left) {
          return;
        }

        // A refresh that fails keeps the list it had.
        if (!isRefresh) {
          models.value = [];
          modelsByKey.value = {};
        }
        error.value = fetchError instanceof Error ? fetchError.message : "Failed to load models";
      } finally {
        if (!left) {
          isLoading.value = false;
        }
      }
    },
    { immediate: true },
  );

  return {
    models: computed(() => models.value),
    modelsByKey: computed(() => modelsByKey.value),
    defaultModelKey,
    isLoading: readonly(isLoading),
    error: readonly(error),
  };
}
