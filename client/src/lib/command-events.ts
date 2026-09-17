export type CommandEventName =
  | "weave:command-focus-prompt"
  | "weave:command-scroll-top"
  | "weave:command-scroll-bottom"
  | "weave:command-copy-session-id"
  | "weave:command-export-conversation"
  /** Scroll the conversation to a message and mark it briefly: detail is { sessionId, messageId }. */
  | "weave:command-show-message";

export function dispatchCommandEvent(name: CommandEventName, detail?: unknown): void {
  if (typeof window === "undefined") {
    return;
  }

  window.dispatchEvent(new CustomEvent(name, { detail }));
}
