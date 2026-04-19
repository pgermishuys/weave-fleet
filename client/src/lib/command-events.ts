export type CommandEventName =
  | "weave:command-focus-prompt"
  | "weave:command-scroll-top"
  | "weave:command-scroll-bottom"
  | "weave:command-copy-session-id"
  | "weave:command-export-conversation";

export function dispatchCommandEvent(name: CommandEventName, detail?: unknown): void {
  if (typeof window === "undefined") {
    return;
  }

  window.dispatchEvent(new CustomEvent(name, { detail }));
}
