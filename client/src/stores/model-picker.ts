import { defineStore } from "pinia";
import { computed } from "vue";
import { usePreferencesStore } from "@/stores/preferences";

const PREFERENCE_KEY = "modelPicker";
const RECENT_PER_HARNESS = 5;

interface ModelPickerState {
  favourites: string[];
  hidden: string[];
  recent: string[];
}

/** One entry per model per harness. Only ever compared, never parsed back. */
export function modelPickerKey(harnessType: string, providerId: string, modelId: string): string {
  return `${harnessType}:${providerId}/${modelId}`;
}

function strings(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((entry): entry is string => typeof entry === "string") : [];
}

function parse(raw: string): ModelPickerState {
  try {
    const value = JSON.parse(raw) as Partial<Record<keyof ModelPickerState, unknown>> | null;
    return { favourites: strings(value?.favourites), hidden: strings(value?.hidden), recent: strings(value?.recent) };
  } catch {
    return { favourites: [], hidden: [], recent: [] };
  }
}

const harnessOf = (key: string): string => key.slice(0, key.indexOf(":"));

/** Starred and recently used models, kept with the other preferences so they follow the user between devices. */
export const useModelPickerStore = defineStore("model-picker", () => {
  const preferences = usePreferencesStore();
  preferences.ensureLoaded();

  const state = computed(() => parse(preferences.get(PREFERENCE_KEY, "")));
  const favourites = computed<readonly string[]>(() => state.value.favourites);
  const recent = computed<readonly string[]>(() => state.value.recent);

  /** Applies a change to what is stored. Waits for the preferences to load first, so a write never replaces what it hasn't read. */
  async function change(update: (current: ModelPickerState) => ModelPickerState): Promise<void> {
    if (!preferences.hasFetched) await preferences.refresh();
    const next = JSON.stringify(update(state.value));
    // `hidden` and keys the model list no longer has stay as they were; nothing here prunes.
    if (next === JSON.stringify(state.value)) return;
    await preferences.set(PREFERENCE_KEY, next);
  }

  function isFavourite(key: string): boolean {
    return state.value.favourites.includes(key);
  }

  function toggleFavourite(key: string): Promise<void> {
    return change((current) => ({
      ...current,
      favourites: current.favourites.includes(key)
        ? current.favourites.filter((entry) => entry !== key)
        : [...current.favourites, key],
    }));
  }

  function recordRecent(key: string): Promise<void> {
    const harness = harnessOf(key);
    return change((current) => {
      const mine = [key, ...current.recent.filter((entry) => entry !== key && harnessOf(entry) === harness)]
        .slice(0, RECENT_PER_HARNESS);
      // Other harnesses' entries keep their place.
      return { ...current, recent: [...mine, ...current.recent.filter((entry) => harnessOf(entry) !== harness)] };
    });
  }

  return { favourites, recent, isFavourite, toggleFavourite, recordRecent };
});
