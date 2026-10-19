import { computed, type MaybeRefOrGetter, toValue } from "vue";
import {
  DEFAULT_PERMISSION_LEVEL,
  PERMISSION_LEVEL_KEY,
  harnessPermissionKey,
  toPermissionLevel,
  type PermissionLevel,
} from "@/lib/permissions";
import { usePreferencesStore } from "@/stores/preferences";

/** The level a harness's sessions run at now (Settings → Permissions): its own, else the default. */
export function usePermissionLevel(harnessType: MaybeRefOrGetter<string | undefined>) {
  const preferencesStore = usePreferencesStore();
  preferencesStore.ensureLoaded();
  return computed<PermissionLevel>(() => {
    const type = toValue(harnessType);
    const own = type ? toPermissionLevel(preferencesStore.preferences[harnessPermissionKey(type)]) : null;
    return own ?? toPermissionLevel(preferencesStore.preferences[PERMISSION_LEVEL_KEY]) ?? DEFAULT_PERMISSION_LEVEL;
  });
}
