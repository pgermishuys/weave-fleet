/** How the phone words an agent's permission ask: its heading, and what "Don't ask again" would allow. No Vue. */
import type { PermissionAsk } from "@/composables/use-session-permissions";

/** "Run a command", "Edit a file", …: what the agent wants, as the ask's heading. */
export function permissionTitle(ask: Pick<PermissionAsk, "kind" | "tool">): string {
  switch (ask.kind) {
    case "shell": return "Run a command";
    case "edit": return "Edit a file";
    case "read": return "Read a file";
    case "web": return "Go online";
    default: return ask.tool === "external_directory" ? "Work outside the folder" : `Use ${ask.tool}`;
  }
}

/** The docked ask's one line: "Wants to run a command", … (until the docked ask takes the desktop card's head). */
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
 * What "Don't ask again" covers, as the desktop PermissionCard words it: the harness's patterns ("Don't ask again for
 * `dotnet test *`"), or the whole kind of thing when there are none.
 */
export function dontAskAgain(ask: Pick<PermissionAsk, "kind" | "tool" | "always">): { lead: string; code: string | null } {
  const patterns = ask.always.filter((pattern) => pattern !== "*");
  if (patterns.length > 0) return { lead: "Don't ask again for", code: patterns.join(", ") };
  switch (ask.kind) {
    case "edit": return { lead: "Don't ask again for file edits", code: null };
    case "shell": return { lead: "Don't ask again for commands", code: null };
    case "web": return { lead: "Don't ask again for web access", code: null };
    default: return { lead: "Don't ask again for", code: ask.tool };
  }
}
