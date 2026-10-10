import type { AccumulatedMessage, AccumulatedPart } from "@/lib/client-types";

/**
 * The pure parts of building a conversation's message views (`useActivityMessages`): the text a message shows, who
 * sent it, and how a run of messages from one sender clusters.
 */

export type ClusterPosition = "single" | "first" | "middle" | "last";

/** What clustering reads of a message view. */
export interface ClusterableMessage {
  role: string;
  senderKey: string;
}

// A streamed token replaces only the message it belongs to; every other message keeps its object. What's derived from a
// message is kept with it, so an unchanged message hands its bubble the same props and the bubble doesn't re-render.
const bodies = new WeakMap<AccumulatedMessage, string>();

/** The text a message shows, kept with the message object it was made from. */
export function messageBody(message: AccumulatedMessage): string {
  let body = bodies.get(message);
  if (body === undefined) {
    body = renderMessageBody(message.parts);
    bodies.set(message, body);
  }
  return body;
}

export function renderMessageBody(parts: readonly AccumulatedPart[]): string {
  const bodyParts = parts
    .map((part) => renderMessagePart(part))
    .filter((part): part is string => part !== null);

  return bodyParts.join("\n\n");
}

function renderMessagePart(part: AccumulatedPart): string | null {
  if (part.type === "text") {
    return part.text;
  }

  if (part.type === "reasoning") {
    // Reasoning is now rendered separately, not in the markdown body
    return null;
  }

  if (part.type === "file") {
    if (part.mime.startsWith("image/")) {
      return null; // Images are rendered as thumbnails, not inline text
    }
    const label = part.filename?.trim() || "Attached file";
    return part.url ? `[${label}](${part.url})` : label;
  }

  return null;
}

export function getDisplayAuthor(message: AccumulatedMessage): string {
  if (message.role === "user") {
    return "You";
  }

  return formatAgentDisplayName(message.agent ?? "Assistant");
}

export function getSenderKey(role: AccumulatedMessage["role"], author?: string | null): string {
  if (role === "user") {
    return "user";
  }

  return normalizeIdentity(author ?? "Assistant");
}

export function getClusterPosition(groupedWithPrevious: boolean, groupedWithNext: boolean): ClusterPosition {
  if (groupedWithPrevious && groupedWithNext) {
    return "middle";
  }

  if (groupedWithPrevious) {
    return "last";
  }

  if (groupedWithNext) {
    return "first";
  }

  return "single";
}

export function isSameSender(previous: ClusterableMessage | undefined, next: ClusterableMessage | undefined): boolean {
  if (!previous || !next) {
    return false;
  }

  return previous.role === next.role && previous.senderKey === next.senderKey;
}

export function formatAgentDisplayName(author: string): string {
  const normalizedAuthor = author.replace(/[_-]+/g, " ").replace(/\s+/g, " ").trim();

  if (!normalizedAuthor) {
    return "Assistant";
  }

  if (normalizedAuthor.toLowerCase() !== normalizedAuthor) {
    return normalizedAuthor;
  }

  return normalizedAuthor.replace(/(^|[\s(])([a-z])/g, (_match, prefix: string, character: string) => {
    return `${prefix}${character.toUpperCase()}`;
  });
}

function normalizeIdentity(value: string): string {
  return value.replace(/[_-]+/g, " ").replace(/\s+/g, " ").trim().toLowerCase();
}

export function hasRenderableAssistantContent(message: AccumulatedMessage): boolean {
  if (message.role !== "assistant") {
    return false;
  }

  return message.parts.some((part) => {
    if (part.type === "tool") {
      return true;
    }

    if (part.type === "file") {
      return Boolean(part.filename?.trim() || part.url);
    }

    if (part.type === "text") {
      return part.text.trim().length > 0;
    }

    if (part.type === "reasoning") {
      return ((part.summary ?? part.text) || "").trim().length > 0;
    }

    return false;
  });
}

export function sameItems(left: readonly unknown[], right: readonly unknown[]): boolean {
  return left.length === right.length && left.every((item, index) => item === right[index]);
}

export function sameEntries<K, V>(left: ReadonlyMap<K, V>, right: ReadonlyMap<K, V>): boolean {
  if (left.size !== right.size) {
    return false;
  }

  for (const [key, value] of left) {
    if (right.get(key) !== value) {
      return false;
    }
  }

  return true;
}
