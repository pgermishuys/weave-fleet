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

/** Whether something runs without asking, as Settings → What agents can do shows a level. */
export type Reach = "allowed" | "ask";

/** A level as its two rows on What agents can do: file edits, and commands (the shell, the web and the browser). */
export interface LevelReach {
  edits: Reach;
  commands: Reach;
}

export function reachOf(level: PermissionLevel): LevelReach {
  if (level === "ask") return { edits: "ask", commands: "ask" };
  if (level === "edits") return { edits: "allowed", commands: "ask" };
  return { edits: "allowed", commands: "allowed" };
}

/**
 * The level after one row changes. No level lets commands run unasked while edits ask (a command can edit files), so
 * the other row follows: Edit files Ask makes commands ask too, and Run commands Allowed allows edits too.
 */
export function levelWith(level: PermissionLevel, row: keyof LevelReach, value: Reach): PermissionLevel {
  const reach = { ...reachOf(level), [row]: value };
  if (row === "edits" && value === "ask") return "ask";
  if (reach.commands === "allowed") return "all";
  return reach.edits === "allowed" ? "edits" : "ask";
}
