/** How the phone words an agent's permission ask: the sheet's title, and what "always" would allow. No Vue. */
import type { PermissionAsk } from "@/composables/use-session-permissions";

/** "Run a command", "Edit a file", …: what the agent wants, as the ask's heading. */
export function permissionTitle(ask: Pick<PermissionAsk, "kind" | "tool">): string {
  switch (ask.kind) {
    case "shell": return "Run a command";
    case "edit": return "Edit a file";
    case "read": return "Read a file";
    case "web": return "Open a web page";
    default: return `Use ${ask.tool}`;
  }
}

/** The docked ask's one line: "Wants to run a command", "Wants to edit a file", … */
export function permissionWants(ask: Pick<PermissionAsk, "kind" | "tool">): string {
  switch (ask.kind) {
    case "shell": return "Wants to run a command";
    case "edit": return "Wants to edit a file";
    case "read": return "Wants to read a file";
    case "web": return "Wants to open a web page";
    default: return `Wants to use ${ask.tool}`;
  }
}

/**
 * What "Always allow in this session" covers, from the ask's first pattern: a trailing `*` reads as "starts with"
 * ("Commands that start with `dotnet test`"), anything else as itself.
 */
export function alwaysCovers(ask: Pick<PermissionAsk, "kind" | "tool" | "always">): { lead: string; code: string } {
  const pattern = ask.always[0]?.trim() || ask.tool;
  const prefix = /\s*\*$/.test(pattern) ? pattern.replace(/\s*\*$/, "") : null;
  if (prefix) return { lead: ask.kind === "shell" ? "Commands that start with" : "Anything that starts with", code: prefix };
  return { lead: ask.kind === "shell" ? "The command" : "Anything matching", code: pattern };
}
