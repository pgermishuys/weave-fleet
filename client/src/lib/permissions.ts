/** Settings → Permissions, as Fleet's preferences keep it (the server's `SessionPermissions`). */

export type PermissionLevel = "ask" | "edits" | "all";
export type UnattendedPermission = "all" | "same" | "deny";

/** The level every harness uses unless one is set for it. Unset, nothing asks. */
export const PERMISSION_LEVEL_KEY = "PermissionLevel";

/** What happens in a run nobody watches: an automation's session or a workflow step. */
export const PERMISSION_UNATTENDED_KEY = "PermissionUnattended";

/** The preference for one harness's own level; unset means the default. */
export function harnessPermissionKey(harnessType: string): string {
  return `${PERMISSION_LEVEL_KEY}.${harnessType}`;
}

export const DEFAULT_PERMISSION_LEVEL: PermissionLevel = "all";
export const DEFAULT_UNATTENDED_PERMISSION: UnattendedPermission = "all";

export function toPermissionLevel(value: string | undefined): PermissionLevel | null {
  return value === "ask" || value === "edits" || value === "all" ? value : null;
}

export function toUnattendedPermission(value: string | undefined): UnattendedPermission {
  return value === "same" || value === "deny" ? value : DEFAULT_UNATTENDED_PERMISSION;
}

export const PERMISSION_LEVEL_NAMES: Record<PermissionLevel, string> = {
  ask: "Ask before changes",
  edits: "Allow edits",
  all: "Allow everything",
};
