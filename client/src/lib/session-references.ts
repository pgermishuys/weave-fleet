/**
 * `@` sessions: a session picked from the composer's `@` list goes into the draft as a token made from its title
 * (`@t3code-what-can-we-learn`), and the composer remembers which session each token names. On send the tokens still
 * in the text go to the server with the session ids, and Fleet adds a block after the text naming each session, for
 * the agent: a link it reads with `fleet_session_read`, or a recap where the harness has no Fleet tools.
 *
 *   Use the subagent mapping from @t3code-what-can-we-learn
 *
 *   <fleet-session-references>
 *   …what the block is…
 *   <session ref="@t3code-what-can-we-learn" id="…" title="t3code: what can we learn?" />
 *   </fleet-session-references>
 *
 * The block is in the text itself, so it survives a reload from the harness's store; the conversation hides it and
 * shows each token as a chip with the session's title.
 */
import { reactive } from "vue";
import type { SessionListItem } from "@/api/client";
import { readStoredDraft, writeStoredDraft } from "@/lib/draft-storage";

export interface SessionReference {
  /** The token in the text, `@` included. */
  token: string;
  sessionId: string;
  title: string;
}

/** The most references one composer remembers; the oldest go first. */
const MAX_REMEMBERED = 50;
const MAX_SLUG_LENGTH = 40;

/** A session's title as a token: lower case, words joined by dashes, `@session` when nothing is left. */
export function sessionReferenceSlug(title: string): string {
  const words = title
    .normalize("NFKD")
    .replace(/[̀-ͯ]/g, "")
    .toLowerCase()
    .split(/[^\p{L}\p{N}]+/u)
    .filter(Boolean);

  let slug = "";
  for (const word of words) {
    const next = slug ? `${slug}-${word}` : word;
    if (next.length > MAX_SLUG_LENGTH) {
      if (!slug) slug = word.slice(0, MAX_SLUG_LENGTH);
      break;
    }
    slug = next;
  }

  return slug || "session";
}

const ESCAPE = /[.*+?^${}()|[\]\\]/g;

/** Whether `token` stands on its own in `text`: `@notes` isn't in `@notes-2` or `@notes.md`, but is in `@notes.` */
export function textHasToken(text: string, token: string): boolean {
  return tokenPattern(token).test(text);
}

function tokenPattern(token: string, flags = "u"): RegExp {
  return new RegExp(`(^|[\\s(\\["'])${token.replace(ESCAPE, "\\$&")}(?![\\p{L}\\p{N}_\\-/@]|\\.[\\p{L}\\p{N}])`, flags);
}

// ── The references each composer picked ───────────────────────────────────

/** Each composer's picks, by the session the composer sends to. Kept in the browser with the draft. */
const remembered = reactive<Record<string, SessionReference[]>>({});

const storageKey = (draftSessionId: string) => `session-refs.${draftSessionId}`;

function isReference(value: unknown): value is SessionReference {
  if (!value || typeof value !== "object") return false;
  const { token, sessionId, title } = value as Record<string, unknown>;
  return typeof token === "string" && typeof sessionId === "string" && typeof title === "string";
}

function picksFor(draftSessionId: string): SessionReference[] {
  let picks = remembered[draftSessionId];
  if (!picks) {
    let stored: unknown = [];
    try {
      stored = JSON.parse(readStoredDraft(storageKey(draftSessionId)) ?? "[]");
    } catch {
      stored = [];
    }
    remembered[draftSessionId] = Array.isArray(stored) ? stored.filter(isReference) : [];
    picks = remembered[draftSessionId];
  }
  return picks;
}

/**
 * The token for `session` in a composer: its slug, or the slug with `-2`, `-3`… when another session in the same
 * composer already has it. A session picked before gets its token back.
 */
export function sessionReferenceToken(draftSessionId: string, session: { id: string; title: string }): string {
  const picks = picksFor(draftSessionId);
  const base = `@${sessionReferenceSlug(session.title)}`;
  for (let attempt = 1; ; attempt += 1) {
    const token = attempt === 1 ? base : `${base}-${attempt}`;
    const holder = picks.find((pick) => pick.token === token);
    if (!holder || holder.sessionId === session.id) return token;
  }
}

/** Remembers that `reference.token` in this composer names `reference.sessionId`. */
export function rememberSessionReference(draftSessionId: string, reference: SessionReference): void {
  const picks = picksFor(draftSessionId).filter((pick) => pick.token !== reference.token);
  picks.push({ token: reference.token, sessionId: reference.sessionId, title: reference.title });
  remembered[draftSessionId] = picks.slice(-MAX_REMEMBERED);
  writeStoredDraft(storageKey(draftSessionId), JSON.stringify(remembered[draftSessionId]));
}

/** The picks whose token is still in `text`, in the order they appear. What goes with the message. */
export function sessionReferencesIn(draftSessionId: string, text: string): SessionReference[] {
  return picksFor(draftSessionId)
    .filter((pick) => textHasToken(text, pick.token))
    .sort((a, b) => text.search(tokenPattern(a.token)) - text.search(tokenPattern(b.token)));
}

/** Every token this composer has picked (reactive): the draft tints them as sessions. */
export function rememberedSessionTokens(draftSessionId: string): ReadonlySet<string> {
  return new Set(picksFor(draftSessionId).map((pick) => pick.token));
}

/** For tests: forgets every composer's picks. */
export function resetSessionReferences(): void {
  for (const key of Object.keys(remembered)) delete remembered[key];
}

// ── The block in a sent message ───────────────────────────────────────────

const BLOCK = /\s*<fleet-session-references>\n[\s\S]*?<\/fleet-session-references>\s*$/;
const ENTRY = /<session ref="([^"]*)" id="([^"]*)" title="([^"]*)"/g;

// Fleet HTML-encodes the attributes.
const ENTITIES: Record<string, string> = { "&amp;": "&", "&quot;": "\"", "&#39;": "'", "&lt;": "<", "&gt;": ">" };

function decode(value: string): string {
  return value.replace(/&(amp|quot|#39|lt|gt);/g, (entity) => ENTITIES[entity]);
}

/** A sent message's text without the block Fleet added, and the sessions the block names. */
export function parseSessionReferences(body: string): { text: string; references: SessionReference[] } {
  const block = BLOCK.exec(body);
  if (!block) return { text: body, references: [] };

  const references = [...block[0].matchAll(ENTRY)].map((entry) => ({
    token: decode(entry[1]),
    sessionId: decode(entry[2]),
    title: decode(entry[3]),
  }));
  return { text: body.slice(0, block.index), references };
}

/** A message's text as the user wrote it: without the block Fleet added for the agent. */
export function stripSessionReferences(body: string): string {
  return parseSessionReferences(body).text;
}

// ── Chips in a rendered message ──────────────────────────────────────────

// lucide's messages-square, so the chip needs no component inside rendered Markdown.
const CHIP_ICON = '<svg class="session-ref-chip__icon" xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" '
  + 'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">'
  + '<path d="M14 9a2 2 0 0 1-2 2H6l-4 4V4a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2z"/>'
  + '<path d="M18 9h2a2 2 0 0 1 2 2v11l-4-4h-6a2 2 0 0 1-2-2v-1"/></svg>';

function escapeHtml(value: string): string {
  return value.replace(/[&<>"']/g, (character) => `&${({ "&": "amp", "<": "lt", ">": "gt", "\"": "quot", "'": "#39" } as const)[character as "&"]};`);
}

function chip(reference: SessionReference): string {
  const id = escapeHtml(reference.sessionId);
  const title = escapeHtml(reference.title);
  return `<a class="session-ref-chip" href="/sessions/${encodeURIComponent(reference.sessionId)}" data-session-ref="${id}" `
    + `title="Open ${title}">${CHIP_ICON}<span class="session-ref-chip__title">${title}</span></a>`;
}

/**
 * Rendered Markdown with each reference's token drawn as a chip: the session's title, linking to it. Tokens inside
 * code are left as they are.
 */
export function withSessionReferenceChips(html: string, references: readonly SessionReference[]): string {
  if (references.length === 0) return html;

  let inCode = 0;
  return html
    .split(/(<[^>]+>)/)
    .map((piece) => {
      if (piece.startsWith("<")) {
        if (/^<(code|pre)\b/i.test(piece)) inCode += 1;
        else if (/^<\/(code|pre)>/i.test(piece)) inCode = Math.max(0, inCode - 1);
        return piece;
      }
      if (inCode > 0 || !piece.includes("@")) return piece;
      return references.reduce(
        (text, reference) => text.replace(
          tokenPattern(escapeHtml(reference.token), "gu"),
          (_match, before: string) => `${before}${chip(reference)}`,
        ),
        piece,
      );
    })
    .join("");
}

// ── The @ list ────────────────────────────────────────────────────────────

/** A session offered in the `@` list. */
export interface ReferableSession {
  id: string;
  title: string;
  /** The row's status, as the session list has it (`active`, `waiting_input`, `idle`…). */
  status: string;
  activity?: string | null;
  /** Where it is and what runs it: "weave-fleet · OpenCode 2 · archived", or "subagent of …". */
  description: string;
  /** When it was last active, for the age on the right. */
  updatedAt: number;
}

function timestamp(value: number | string | undefined | null): number {
  if (typeof value === "number") return value;
  if (!value) return 0;
  const parsed = Number(value) || Date.parse(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

function folderName(directory: string): string {
  return directory.replace(/[\\/]+$/, "").split(/[\\/]/).pop() ?? directory;
}

/**
 * The sessions to offer for `query`, best first: titles that start with it, then titles with a word that does, then
 * any title containing it, newest first within each. An empty query offers the newest. Leaves out `excludeId` (the
 * session being written to).
 */
export function matchReferableSessions(
  items: readonly SessionListItem[],
  query: string,
  options: { excludeId?: string | null; limit: number; harnessName?: (type: string) => string | undefined },
): ReferableSession[] {
  const needle = query.trim().toLowerCase();
  const titles = new Map(items.map((item) => [item.session.id, item.session.title]));
  const seen = new Set<string>();

  const ranked: { item: SessionListItem; rank: number; updated: number }[] = [];
  for (const item of items) {
    const id = item.session.id;
    if (id === options.excludeId || seen.has(id)) continue;
    seen.add(id);

    const title = (item.session.title ?? "").toLowerCase();
    let rank = 0;
    if (needle) {
      if (title.startsWith(needle)) rank = 0;
      else if (title.split(/[^\p{L}\p{N}]+/u).some((word) => word.startsWith(needle))) rank = 1;
      else if (title.includes(needle)) rank = 2;
      else continue;
    }
    ranked.push({ item, rank, updated: timestamp(item.session.time?.updated ?? item.session.time?.created) });
  }

  ranked.sort((a, b) => a.rank - b.rank || b.updated - a.updated);

  return ranked.slice(0, options.limit).map(({ item, updated }) => {
    const parentTitle = item.parentSessionId ? titles.get(item.parentSessionId) : undefined;
    const harness = options.harnessName?.(item.harnessType) ?? item.harnessType;
    const place = item.projectName ?? item.workspaceDisplayName ?? folderName(item.workspaceDirectory);
    const parts = parentTitle ? [`subagent of ${parentTitle}`] : [place, harness];
    if (item.retentionStatus === "archived") parts.push("archived");

    return {
      id: item.session.id,
      title: item.session.title,
      status: item.sessionStatus,
      activity: item.activityStatus,
      description: parts.filter(Boolean).join(" · "),
      updatedAt: updated,
    };
  });
}
