import { ref, shallowRef } from "vue";
import { defineMachineStore } from "@/lib/machine-store";

/** The preferences of a machine (the live one unless asked for another); see `defineMachineStore`. */
export const usePreferencesStore = defineMachineStore("preferences", (target) => {
  const preferences = ref<Record<string, string>>({});
  const isLoading = shallowRef(false);
  const hasFetched = shallowRef(false);
  /** The last read didn't get an answer: what's shown are the defaults, not the machine's own values. */
  const loadFailed = shallowRef(false);

  async function fetchPreferences(): Promise<void> {
    isLoading.value = true;

    try {
      const { data, error: apiError } = await target.api.GET("/api/preferences");
      if (apiError || !data) {
        loadFailed.value = true;
        return;
      }

      preferences.value = data as Record<string, string>;
      loadFailed.value = false;
    } catch {
      // Unreachable: the fallbacks are used, and `loadFailed` says so.
      loadFailed.value = true;
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
      const { error: apiError } = await target.api.PUT("/api/preferences/{key}", {
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
    loadFailed,
    get,
    set,
    refresh: fetchPreferences,
    ensureLoaded,
  };
});
