import { defineStore } from "pinia";
import { ref, shallowRef } from "vue";
import { api } from "@/api/client";

export const usePreferencesStore = defineStore("preferences", () => {
  const preferences = ref<Record<string, string>>({});
  const isLoading = shallowRef(false);
  const hasFetched = shallowRef(false);

  async function fetchPreferences(): Promise<void> {
    isLoading.value = true;

    try {
      const { data, error: apiError } = await api.GET("/api/preferences");
      if (apiError) {
        return;
      }

      if (!data) {
        return;
      }

      preferences.value = data as Record<string, string>;
    } catch {
      // Silently fail — will use fallbacks
    } finally {
      isLoading.value = false;
      hasFetched.value = true;
    }
  }

  function get(key: string, fallback: string): string {
    return preferences.value[key] ?? fallback;
  }

  async function set(key: string, value: string): Promise<void> {
    const before = preferences.value[key];
    preferences.value = { ...preferences.value, [key]: value };

    try {
      const { error: apiError } = await api.PUT("/api/preferences/{key}", {
        params: { path: { key } },
        body: { value } as never,
      });
      if (!apiError) return;
    } catch {
      // Offline: handled below, like an error answer.
    }

    // The save failed: show what the server still has, even when it can't be asked.
    if (preferences.value[key] === value) {
      const restored = { ...preferences.value };
      if (before === undefined) delete restored[key];
      else restored[key] = before;
      preferences.value = restored;
    }
    await fetchPreferences();
  }

  function ensureLoaded(): void {
    if (!hasFetched.value && !isLoading.value) {
      void fetchPreferences();
    }
  }

  return {
    preferences,
    isLoading,
    hasFetched,
    get,
    set,
    refresh: fetchPreferences,
    ensureLoaded,
  };
});
