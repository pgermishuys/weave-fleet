export type UpdateChannel = "stable" | "dev";

export interface UpdatePreferences {
  autoUpdate: boolean;
  channel: UpdateChannel;
}

export const DEFAULT_UPDATE_PREFERENCES: UpdatePreferences = {
  autoUpdate: false,
  channel: "stable",
};

const UPDATE_PREFERENCES_KEY = "weave:update-preferences";

function readUpdatePreferences(): UpdatePreferences {
  try {
    const raw = localStorage.getItem(UPDATE_PREFERENCES_KEY);
    if (!raw) {
      return DEFAULT_UPDATE_PREFERENCES;
    }

    const parsed = JSON.parse(raw) as Partial<UpdatePreferences>;
    return {
      autoUpdate: parsed.autoUpdate ?? DEFAULT_UPDATE_PREFERENCES.autoUpdate,
      channel: parsed.channel ?? DEFAULT_UPDATE_PREFERENCES.channel,
    };
  } catch {
    return DEFAULT_UPDATE_PREFERENCES;
  }
}

function writeUpdatePreferences(next: UpdatePreferences): void {
  try {
    localStorage.setItem(UPDATE_PREFERENCES_KEY, JSON.stringify(next));
  } catch {
    // localStorage unavailable
  }
}

export function useUpdatePreferences(): [
  UpdatePreferences,
  (next: UpdatePreferences | ((prev: UpdatePreferences) => UpdatePreferences)) => void,
] {
  const current = readUpdatePreferences();

  return [
    current,
    (next) => {
      const resolved = typeof next === "function"
        ? next(readUpdatePreferences())
        : next;

      writeUpdatePreferences(resolved);
    },
  ];
}
