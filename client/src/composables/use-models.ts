import { storeToRefs } from "pinia";
import { computed, readonly, ref, shallowRef, watch } from "vue";
import { apiFetch } from "@/lib/api-client";
import type { AvailableProvider } from "@/lib/api-types";
import { useSessionsStore } from "@/stores/sessions";

export interface ModelOption {
  id: string;
  name: string;
  providerId: string;
  selectionKey: string;
  provider: string;
  description: string;
}

export function createModelSelectionKey(providerId: string, modelId: string): string {
  return JSON.stringify([providerId, modelId]);
}

function toModelOptions(providers: readonly AvailableProvider[]): ModelOption[] {
  return providers.flatMap((provider) => {
    return provider.models.map((model) => ({
      id: model.id,
      name: model.name,
      providerId: provider.id,
      selectionKey: createModelSelectionKey(provider.id, model.id),
      provider: provider.name,
      description: "",
    }));
  });
}

export function useModels(sessionId?: string) {
  const sessionsStore = useSessionsStore();
  const { sessions, activeSessionId } = storeToRefs(sessionsStore);

  const models = ref<ModelOption[]>([]);
  const modelsByKey = ref<Record<string, ModelOption>>({});
  const isLoading = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);

  const resolvedSessionId = computed(() => sessionId ?? activeSessionId.value ?? "");
  const instanceId = computed(() => {
    return sessions.value.find((session) => session.session.id === resolvedSessionId.value)?.instanceId ?? "";
  });
  const defaultModelKey = computed(() => models.value[0]?.selectionKey ?? "");

  watch(
    instanceId,
    async (nextInstanceId, _previous, onCleanup) => {
        if (!nextInstanceId) {
          models.value = [];
          modelsByKey.value = {};
          isLoading.value = false;
          error.value = undefined;
          return;
        }

      const controller = new AbortController();
      onCleanup(() => {
        controller.abort();
      });

      isLoading.value = true;
      error.value = undefined;

      try {
        const response = await apiFetch(`/api/instances/${encodeURIComponent(nextInstanceId)}/models`, {
          signal: controller.signal,
        });

        if (!response.ok) {
          const payload = (await response.json().catch(() => ({}))) as { error?: string };
          throw new Error(payload.error ?? `HTTP ${response.status}`);
        }

        const body = (await response.json()) as { providers?: AvailableProvider[] } | AvailableProvider[];
        const providers = Array.isArray(body) ? body : body.providers ?? [];
        const nextModels = toModelOptions(providers);

        models.value = nextModels;
        modelsByKey.value = Object.fromEntries(nextModels.map((model) => [model.selectionKey, model])) as Record<string, ModelOption>;
      } catch (fetchError) {
        if (fetchError instanceof DOMException && fetchError.name === "AbortError") {
          return;
        }

        models.value = [];
        modelsByKey.value = {};
        error.value = fetchError instanceof Error ? fetchError.message : "Failed to load models";
      } finally {
        isLoading.value = false;
      }
    },
    { immediate: true },
  );

  return {
    models: readonly(models),
    modelsByKey: readonly(modelsByKey),
    defaultModelKey,
    isLoading: readonly(isLoading),
    error: readonly(error),
  };
}
