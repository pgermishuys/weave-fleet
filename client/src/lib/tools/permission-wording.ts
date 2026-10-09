/**
 * How an agent's permission ask is worded, in one place: the heading, what "Don't ask again" covers, and the inbox's
 * one-line preview. The kinds are the server's (`Permissions.Classify`): read, edit, shell, web, other.
 */
export type PermissionKindName = "read" | "edit" | "shell" | "web" | "other";

interface Ask {
  kind: PermissionKindName | string;
  tool: string;
  always?: readonly string[];
  title?: string | null;
}

/** "Run a command", "Edit a file", …: what the agent wants, as the ask's heading. */
export function permissionHeading(ask: Pick<Ask, "kind" | "tool">): string {
  switch (ask.kind) {
    case "shell": return "Run a command";
    case "edit": return "Edit a file";
    case "read": return "Read a file";
    case "web": return "Go online";
    default: return ask.tool === "external_directory" ? "Work outside the folder" : `Use ${ask.tool}`;
  }
}

/** What "Don't ask again" covers: the harness's patterns, or the whole kind of thing when there are none. */
export function dontAskAgainWording(ask: Pick<Ask, "kind" | "tool" | "always">): { lead: string; code: string | null } {
  const patterns = (ask.always ?? []).filter((pattern) => pattern !== "*");
  if (patterns.length > 0) return { lead: "Don't ask again for", code: patterns.join(", ") };
  switch (ask.kind) {
    case "edit": return { lead: "Don't ask again for file edits", code: null };
    case "shell": return { lead: "Don't ask again for commands", code: null };
    case "web": return { lead: "Don't ask again for web access", code: null };
    default: return { lead: "Don't ask again for", code: ask.tool };
  }
}

/** The one-line preview of a pending ask: "Wants to run `git push`". */
export function askPreviewWording(ask: Pick<Ask, "kind" | "tool" | "title">): { lead: string; detail: string | null; code: boolean } {
  const what = ask.title || ask.tool;
  switch (ask.kind) {
    case "shell": return { lead: "Wants to run", detail: what, code: true };
    case "edit": return { lead: "Wants to edit", detail: what, code: true };
    case "read": return { lead: "Wants to read", detail: what, code: true };
    case "web": return { lead: "Wants to open", detail: what, code: true };
    default: return { lead: `Wants to use ${ask.tool}`, detail: ask.title ?? null, code: !!ask.title };
  }
}
