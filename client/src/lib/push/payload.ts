/**
 * What a Fleet push carries (version 1): small and generic, so it fits Web Push's ~4 KB and says nothing a lock
 * screen shouldn't. No tokens, ever. See docs/machines.md, "Push".
 *
 * No Vue and no `window`: the service worker imports this.
 */

/** What happened: an ask the agent is waiting on (`permission`, `question`), or a turn that ended. */
export type PushKind = "permission" | "question" | "finished" | "failed" | "workflow";

export const PUSH_KINDS: readonly PushKind[] = ["permission", "question", "finished", "failed", "workflow"];

export interface PushPayloadV1 {
  v: 1;
  machineId: string;
  machineName: string;
  sessionId: string;
  kind: PushKind;
  /** The older, coarser reason: `needs_you`, `finished` or `failed`. */
  reason: string;
  title: string;
  body: string;
  /** Where tapping goes: a path on the home machine, e.g. `/phone/s/<machine>/<session>?ask=<request>`. */
  url: string;
  /** Repeats with the same tag replace each other: `<machineId>:<sessionId>`. */
  tag: string;
  /** The permission or question the agent waits on, when there is one. */
  requestId?: string;
}

export const MAX_TITLE_LENGTH = 80;
export const MAX_BODY_LENGTH = 180;

function text(value: unknown): string | null {
  return typeof value === "string" ? value : null;
}

function clip(value: string, max: number): string {
  return value.length <= max ? value : `${value.slice(0, max - 1).trimEnd()}…`;
}

const PATH_BASE = "https://fleet.invalid";

/**
 * The path when `url` is a path on this origin, else null. Resolved the way a browser would, so `/\evil.example`
 * (which browsers read as `//evil.example`) and the like don't get through.
 */
function samePagePath(url: string | null): string | null {
  if (!url || !url.startsWith("/")) return null;
  try {
    const resolved = new URL(url, PATH_BASE);
    return resolved.origin === PATH_BASE ? `${resolved.pathname}${resolved.search}${resolved.hash}` : null;
  } catch {
    return null;
  }
}

/**
 * Reads a push. Null when it isn't a version 1 Fleet push. Long titles and bodies are clipped; a `url` that isn't a
 * path on this origin is replaced by the phone home page, so a push can't send the phone somewhere else.
 */
export function parsePushPayload(data: unknown): PushPayloadV1 | null {
  if (!data || typeof data !== "object") return null;
  const raw = data as Record<string, unknown>;
  if (raw.v !== 1) return null;

  const machineId = text(raw.machineId);
  const sessionId = text(raw.sessionId);
  const kind = text(raw.kind) as PushKind | null;
  const title = text(raw.title);
  if (!machineId || !sessionId || !title || !kind || !PUSH_KINDS.includes(kind)) return null;

  const safeUrl = samePagePath(text(raw.url)) ?? "/phone";

  return {
    v: 1,
    machineId,
    machineName: text(raw.machineName) ?? "",
    sessionId,
    kind,
    reason: text(raw.reason) ?? "",
    title: clip(title, MAX_TITLE_LENGTH),
    body: clip(text(raw.body) ?? "", MAX_BODY_LENGTH),
    url: safeUrl,
    tag: text(raw.tag) ?? `${machineId}:${sessionId}`,
    ...(text(raw.requestId) ? { requestId: text(raw.requestId)! } : {}),
  };
}
