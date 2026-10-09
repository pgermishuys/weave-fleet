import { defineContributionPoint } from "@/lib/contributions";

/**
 * Commands that act on one session's conversation: the palette, a shortcut or a canvas asks, and the conversation
 * that is on screen for that session answers. A conversation contributes its handlers while it is mounted (and
 * takes them back when it unmounts), so asking for a session nobody shows does nothing.
 *
 * Each command lists what it carries; a command that carries nothing takes no argument.
 */
export interface SessionCommands {
  "focus-prompt": void;
  "copy-session-id": void;
  "export-conversation": void;
  "scroll-top": void;
  "scroll-bottom": void;
  /** Scroll to a message and mark it briefly: by its id, or by the tool call it holds. */
  "show-message": { messageId?: string; toolCallId?: string };
}

export type SessionCommandName = keyof SessionCommands;

export type SessionCommandHandlers = { [Name in SessionCommandName]?: (detail: SessionCommands[Name]) => void };

export interface SessionCommandContribution {
  sessionId: string;
  handlers: SessionCommandHandlers;
}

/** One entry per session on screen; a conversation mounted again for the same session replaces the earlier one. */
export const sessionCommands = defineContributionPoint<SessionCommandContribution>({
  name: "session commands",
  idOf: (contribution) => contribution.sessionId,
});

/** Asks the conversation open for this session to run a command. Nothing happens when there is none. */
export function runSessionCommand<Name extends SessionCommandName>(
  name: Name,
  sessionId: string | null,
  ...detail: SessionCommands[Name] extends void ? [] : [SessionCommands[Name]]
): void {
  if (sessionId === null) return;

  const handler = sessionCommands.get(sessionId)?.handlers[name] as ((detail?: SessionCommands[Name]) => void) | undefined;
  handler?.(detail[0] as SessionCommands[Name]);
}
