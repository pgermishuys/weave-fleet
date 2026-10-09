/**
 * Every command Fleet knows by id. A command, its keybinding and the things that run it by name (the status bar,
 * the settings page) all use these, so a misspelled id is a type error instead of a button that quietly does nothing.
 */
export const COMMAND_IDS = [
  "nav-fleet",
  "nav-board",
  "nav-analytics",
  "nav-settings",
  "nav-go-to-session",
  "nav-next-session",
  "nav-prev-session",
  "new-session",
  "new-session-in-folder",
  "refresh-sessions",
  "go-to-file",
  "focus-prompt",
  "interrupt-session",
  "copy-session-id",
  "fork-session",
  "export-conversation",
  "scroll-to-top",
  "scroll-to-bottom",
  "clear-conversation",
  "toggle-sidebar",
  "toggle-right-panel",
  "toggle-terminal",
  "toggle-diff-view",
  "toggle-activity-filter",
  "cycle-theme",
  "toggle-dark-light",
  "toggle-fullscreen",
  "zoom-in",
  "zoom-out",
  "open-marketplace-panel",
  "report-problem",
  "board-toggle-mode",
] as const;

export type KnownCommandId = (typeof COMMAND_IDS)[number];

/** The "Go to session" sub-commands, one per session. */
export type SessionCommandId = `nav-session-${string}`;

export type CommandId = KnownCommandId | SessionCommandId;
