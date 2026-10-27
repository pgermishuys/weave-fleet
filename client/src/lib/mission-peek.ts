/** The peek's "just now": the last few things an agent did, read from its latest messages. No Vue. */
import type { FleetMessage } from "@/lib/pagination-utils";

export interface PeekEvent {
  key: string;
  /** When the message holding it was written (ISO). */
  at: string;
  text: string;
}

const TARGET_FIELDS = ["command", "pattern", "filePath", "file_path", "path", "url", "description", "query"];
const MAX_LENGTH = 90;

function firstLine(text: string): string {
  const line = text.split("\n", 1)[0].trim();
  return line.length <= MAX_LENGTH ? line : `${line.slice(0, MAX_LENGTH - 1)}…`;
}

function toolLine(name: string, input: unknown): string {
  const fields = input && typeof input === "object" ? input as Record<string, unknown> : {};
  const field = TARGET_FIELDS.find((candidate) => typeof fields[candidate] === "string" && (fields[candidate] as string).trim());
  const target = field ? firstLine(fields[field] as string) : "";
  return target ? `${name.toLowerCase()} · ${target}` : name.toLowerCase();
}

/** The newest `count` tool calls and replies of the assistant, oldest first. */
export function peekEvents(messages: readonly FleetMessage[], count = 6): PeekEvent[] {
  const events: PeekEvent[] = [];
  for (const message of messages) {
    if (message.role !== "assistant") continue;
    message.parts.forEach((part, index) => {
      const key = `${message.id}:${index}`;
      if (part.type === "tool" && part.toolName) events.push({ key, at: message.timestamp, text: toolLine(part.toolName, part.arguments) });
      else if (part.type === "text" && part.text?.trim()) events.push({ key, at: message.timestamp, text: firstLine(part.text) });
    });
  }
  return events.slice(-count);
}
