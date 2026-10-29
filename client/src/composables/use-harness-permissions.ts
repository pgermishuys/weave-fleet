import { computed } from "vue";
import type { HarnessInfo } from "@/api/client";
import {
  DEFAULT_PERMISSION_LEVEL,
  PERMISSION_LEVEL_KEY,
  PERMISSION_LEVEL_NAMES,
  harnessPermissionKey,
  toPermissionLevel,
  type PermissionLevel,
} from "@/lib/permissions";
import { usePreferencesStore } from "@/stores/preferences";

export const PERMISSION_LEVELS: readonly PermissionLevel[] = ["ask", "edits", "all"];

/**
 * What each harness may do without asking, from Fleet's preferences (Settings → Permissions sets the default, a
 * harness's own tab can set its own). Shared by that tab's "What it may do" group and the Permissions page's table.
 */
export function useHarnessPermissions() {
  const preferencesStore = usePreferencesStore();
  preferencesStore.ensureLoaded();

  const defaultLevel = computed<PermissionLevel>(
    () => toPermissionLevel(preferencesStore.preferences[PERMISSION_LEVEL_KEY]) ?? DEFAULT_PERMISSION_LEVEL,
  );

  /** A harness's own level, or null when it uses the default. */
  function ownLevel(type: string): PermissionLevel | null {
    return toPermissionLevel(preferencesStore.preferences[harnessPermissionKey(type)]);
  }

  function effectiveLevel(type: string): PermissionLevel {
    return ownLevel(type) ?? defaultLevel.value;
  }

  function supportsLevels(harness: Pick<HarnessInfo, "capabilities">): boolean {
    return harness.capabilities?.supportsPermissionLevels === true;
  }

  /** What Fleet hands the harness at its level, as the harness says it, or the level's name. */
  function handedText(harness: Pick<HarnessInfo, "type" | "capabilities" | "presentation">): string {
    const level = effectiveLevel(harness.type);
    return harness.presentation?.permissionModes?.[level] ?? PERMISSION_LEVEL_NAMES[level];
  }

  function setHarnessLevel(type: string, value: string): void {
    // "default" clears the harness's own level; the preferences API keeps strings, so an empty one means unset.
    void preferencesStore.set(harnessPermissionKey(type), value === "default" ? "" : value);
  }

  return { defaultLevel, ownLevel, effectiveLevel, supportsLevels, handedText, setHarnessLevel };
}
