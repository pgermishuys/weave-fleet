import { defineStore } from "pinia";
import { ref, shallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";

export const usePreferencesStore = defineStore("preferences", () => {
  const preferences = ref<Record<string, string>>({});
  const isLoading = shallowRef(false);
  const hasFetched = shallowRef(false);

  async function fetchPreferences(): Promise<void> {
    isLoading.value = true;

    try {
      const response = await apiFetch("/api/preferences");
      if (!response.ok) {
        return;
      }

      preferences.value = (await response.json()) as Record<string, string>;
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
    preferences.value = { ...preferences.value, [key]: value };

    try {
      await apiFetch(`/api/preferences/${encodeURIComponent(key)}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ value }),
      });
    } catch {
      // Revert on failure
      await fetchPreferences();
    }
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
