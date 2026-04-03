"use client";

import { usePersistedState } from "@/hooks/use-persisted-state";

export type UpdateChannel = "stable" | "dev";

export interface UpdatePreferences {
  autoUpdate: boolean;
  channel: UpdateChannel;
}

export const DEFAULT_UPDATE_PREFERENCES: UpdatePreferences = {
  autoUpdate: false,
  channel: "stable",
};

export function useUpdatePreferences(): [
  UpdatePreferences,
  (next: UpdatePreferences | ((prev: UpdatePreferences) => UpdatePreferences)) => void,
] {
  return usePersistedState<UpdatePreferences>(
    "weave:update-preferences",
    DEFAULT_UPDATE_PREFERENCES,
  );
}
