/**
 * Where a skill or tool is installed. Global goes into the user's OpenCode config
 * (~/.config/opencode) and every session sees it; project goes into one repository's
 * .opencode folder and only sessions in that repository see it.
 */
export type InstallTarget =
  | { scope: "global" }
  | { scope: "project"; projectPath: string };

export const GLOBAL_TARGET: InstallTarget = { scope: "global" };

/** Builds a target from the `scope`/`projectPath` fields the API returns. */
export function toInstallTarget(
  scope: string | null | undefined,
  projectPath: string | null | undefined,
): InstallTarget {
  return scope === "project" && projectPath ? { scope: "project", projectPath } : GLOBAL_TARGET;
}

export function isSameTarget(left: InstallTarget, right: InstallTarget): boolean {
  if (left.scope === "global" || right.scope === "global") {
    return left.scope === right.scope;
  }
  return left.projectPath === right.projectPath;
}

/** A stable string for keys and select values. */
export function targetKey(target: InstallTarget): string {
  return target.scope === "global" ? "global" : `project:${target.projectPath}`;
}

export function targetFromKey(key: string): InstallTarget {
  return key.startsWith("project:") ? { scope: "project", projectPath: key.slice("project:".length) } : GLOBAL_TARGET;
}

/** Fields for an install request body. */
export function targetBody(target: InstallTarget): { scope: string; projectPath: string | null } {
  return target.scope === "global"
    ? { scope: "global", projectPath: null }
    : { scope: "project", projectPath: target.projectPath };
}

/** Query parameters naming an existing install on update and remove calls. */
export function targetQuery(target: InstallTarget): { scope: string; projectPath?: string } {
  return target.scope === "global" ? { scope: "global" } : { scope: "project", projectPath: target.projectPath };
}

/** "Global", or the repository's folder name. */
export function targetLabel(target: InstallTarget): string {
  if (target.scope === "global") {
    return "Global";
  }
  return target.projectPath.split(/[\\/]/).filter(Boolean).pop() ?? target.projectPath;
}
