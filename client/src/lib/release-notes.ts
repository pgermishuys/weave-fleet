/** One Fleet release, as GET /api/update/releases lists it (newest first). */
export interface ReleaseNote {
  version: string;
  publishedAt: string | null;
  body: string;
  url: string;
}

export interface ReleaseChange {
  title: string;
  /** The part of Fleet it's about, from the conventional-commit scope ("Claude Code" from `fix(claude-code):`). */
  scope?: string;
  /** The pull request, when the line names one. */
  pr?: { number: number; url: string };
}

/** A release's notes split the way What's new shows them. */
export interface ParsedReleaseNotes {
  /** What's written above the change list: highlights, known issues. Markdown. */
  intro: string;
  added: ReleaseChange[];
  fixed: ReleaseChange[];
  /** How many changes were left out as behind the scenes: tests, refactors, docs, CI. */
  internal: number;
}

/** How many versions older than the installed one What's new keeps, folded. */
export const OLDER_VERSIONS_SHOWN = 5;

// GitHub's generated notes: "* <title> by @<user> in https://github.com/<owner>/<repo>/pull/<n>".
const CHANGE_LINE = /^\s*[*-]\s+(.+?)(?:\s+by\s+@[\w-]+(?:\[bot\])?)?\s+in\s+(https:\/\/github\.com\/[\w.-]+\/[\w.-]+\/pull\/(\d+))\s*$/;
const CONVENTIONAL_PREFIX = /^(\w+)(?:\(([^)]*)\))?!?:\s*/;
// Changes nobody using Fleet would notice. They're counted, not listed.
const INTERNAL_TYPES = new Set(["test", "tests", "refactor", "docs", "doc", "chore", "ci", "build", "style", "prototype", "wip"]);
// Scopes that name a layer of Fleet rather than something you'd recognise in it, so they get no label.
const UNLABELLED_SCOPES = new Set(["client", "api", "server", "ui", "release", "app"]);
const SCOPE_LABELS: Record<string, string> = {
  "claude-code": "Claude Code",
  opencode: "OpenCode",
  opencode2: "OpenCode 2",
  github: "GitHub",
  signalr: "SignalR",
  pwa: "PWA",
  mcp: "MCP",
  btw: "/btw",
};
// The fleet-releases mirror adds a link back to the source release at the end.
const MIRROR_FOOTER = /\n-{3,}\s*\nRelease notes from https?:\/\/\S+\s*$/;

/**
 * Splits GitHub's release notes into what was written by hand (above "What's Changed") and the pull requests,
 * grouped into new and fixed by their conventional-commit prefix, which is dropped; its scope becomes a label.
 * Behind-the-scenes changes (tests, refactors, docs, CI) are only counted. The "Full Changelog" line and the
 * "New Contributors" section are left out.
 */
export function parseReleaseNotes(body: string): ParsedReleaseNotes {
  const lines = body.replace(/\r\n/g, "\n").replace(MIRROR_FOOTER, "").split("\n");
  const intro: string[] = [];
  const added: ReleaseChange[] = [];
  const fixed: ReleaseChange[] = [];
  let internal = 0;
  let section: "intro" | "changes" | "other" = "intro";

  for (const line of lines) {
    if (/^\*\*Full Changelog\*\*/.test(line.trim())) continue;
    const heading = line.match(/^#{1,6}\s+(.*)$/);
    if (heading && /what'?s changed/i.test(heading[1])) {
      section = "changes";
      continue;
    }
    // A heading after the change list (New Contributors) ends it; one before it is part of the intro.
    if (heading && section === "changes") {
      section = "other";
      continue;
    }
    if (section === "intro") {
      intro.push(line);
      continue;
    }
    if (section !== "changes" || !line.trim()) continue;

    const change = line.match(CHANGE_LINE);
    const raw = change ? change[1] : line.replace(/^\s*[*-]\s+/, "");
    const prefix = raw.match(CONVENTIONAL_PREFIX);
    const type = prefix?.[1].toLowerCase();
    if (type && INTERNAL_TYPES.has(type)) {
      internal++;
      continue;
    }
    const entry: ReleaseChange = { title: sentenceCase(prefix ? raw.slice(prefix[0].length) : raw) };
    const scope = prefix?.[2] ? scopeLabel(prefix[2]) : undefined;
    if (scope) entry.scope = scope;
    if (change) entry.pr = { number: Number(change[3]), url: change[2] };
    (type === "fix" ? fixed : added).push(entry);
  }

  return { intro: intro.join("\n").trim(), added, fixed, internal };
}

/** "Claude Code" from `claude-code`, "Status bar" from `status-bar`; none for a scope that names a layer (`client`). */
function scopeLabel(scope: string): string | undefined {
  const key = scope.trim().toLowerCase();
  if (!key || UNLABELLED_SCOPES.has(key) || key.includes(",")) return undefined;
  const label = SCOPE_LABELS[key] ?? key.replace(/[-_]+/g, " ");
  return label.charAt(0).toUpperCase() + label.slice(1);
}

/** "3 new, 4 fixed", for a link to the notes; empty when there's nothing to count. */
export function changeSummary(notes: ParsedReleaseNotes): string {
  return [notes.added.length && `${notes.added.length} new`, notes.fixed.length && `${notes.fixed.length} fixed`].filter(Boolean).join(", ");
}

/**
 * The first thing written by hand above the change list, as plain text: a heading or a sentence. A known issue
 * isn't a highlight, so it's skipped.
 */
export function introHighlight(intro: string): string | null {
  for (const line of intro.split("\n")) {
    const text = line
      .replace(/^\s*(?:#{1,6}|[*-]|\d+\.)\s+/, "")
      .replace(/\[([^\]]*)\]\([^)]*\)/g, "$1")
      .replace(/[*_`]/g, "")
      .trim();
    if (!text || /^known issue/i.test(text)) continue;
    return text;
  }
  return null;
}

/** One line of the What's new card. */
export interface WhatsNewItem {
  kind: "highlight" | "new" | "fixed";
  text: string;
  scope?: string;
}

/** What the What's new card says after an update: the versions it covers and the changes most worth a look. */
export interface WhatsNewSummary {
  /** Every release newer than the one Fleet ran before, up to the one it runs now, newest first. */
  releases: ReleaseNote[];
  items: WhatsNewItem[];
  /** Changes in those releases the card leaves out. */
  more: number;
  /** Whether everything left out is a fix. */
  moreAreFixes: boolean;
}

/** How many lines the What's new card lists. */
export const WHATS_NEW_ITEMS = 3;

/**
 * What's new between the version Fleet ran before and the one it runs now: what was written by hand first, then
 * what's new, then what's fixed, newest release first. Null when the notes for the running version aren't there
 * (offline, or not published yet) or have nothing worth a card, so the caller can say less.
 */
export function whatsNewSummary(
  releases: readonly ReleaseNote[],
  previous: string,
  current: string,
  max = WHATS_NEW_ITEMS,
): WhatsNewSummary | null {
  if (!releases.some((r) => compareVersions(r.version, current) === 0)) return null;
  const covered = releases.filter((r) => compareVersions(r.version, previous) > 0 && compareVersions(r.version, current) <= 0);
  const parsed = covered.map((r) => parseReleaseNotes(r.body));

  const highlights: WhatsNewItem[] = parsed.flatMap((notes) => {
    const text = introHighlight(notes.intro);
    return text ? [{ kind: "highlight" as const, text }] : [];
  });
  const item = (kind: "new" | "fixed") => (change: ReleaseChange): WhatsNewItem =>
    change.scope ? { kind, text: change.title, scope: change.scope } : { kind, text: change.title };
  const added = parsed.flatMap((notes) => notes.added.map(item("new")));
  const fixed = parsed.flatMap((notes) => notes.fixed.map(item("fixed")));

  const items = [...highlights, ...added, ...fixed].slice(0, max);
  if (items.length === 0) return null;
  const shownAdded = items.filter((i) => i.kind === "new").length;
  const shownFixed = items.filter((i) => i.kind === "fixed").length;
  const more = added.length - shownAdded + (fixed.length - shownFixed);
  return { releases: covered, items, more, moreAreFixes: added.length === shownAdded };
}

/** The notes as Markdown for Fleet's renderer: the intro, then New and Fixed lists with their pull requests. */
export function releaseNotesMarkdown(notes: ParsedReleaseNotes): string {
  const list = (label: string, changes: ReleaseChange[]) =>
    changes.length === 0
      ? ""
      : `#### ${label}\n\n${changes.map((c) => `- ${c.scope ? `*${c.scope}* ` : ""}${c.title}${c.pr ? ` [#${c.pr.number}](${c.pr.url})` : ""}`).join("\n")}\n`;
  // Headings in the intro sit under the version's own row, so they start a level down.
  const intro = notes.intro.replace(/^#{1,2}\s/gm, "### ");
  return [intro, list("New", notes.added), list("Fixed", notes.fixed)].filter(Boolean).join("\n\n");
}

/** Compares dotted versions numerically: negative when a is older. */
export function compareVersions(a: string, b: string): number {
  const parts = (v: string) => v.replace(/^v/, "").split(/[.+-]/).map((p) => Number.parseInt(p, 10) || 0);
  const [pa, pb] = [parts(a), parts(b)];
  for (let i = 0; i < 3; i++) {
    const diff = (pa[i] ?? 0) - (pb[i] ?? 0);
    if (diff !== 0) return diff;
  }
  return 0;
}

/**
 * Which releases What's new shows: every version newer than the installed one and the installed one itself, open,
 * then up to OLDER_VERSIONS_SHOWN older ones, folded. When none is that new, the newest is open.
 */
export function whatsNewRange(releases: readonly ReleaseNote[], installed: string | null | undefined): {
  open: ReleaseNote[];
  folded: ReleaseNote[];
} {
  const open = installed ? releases.filter((r) => compareVersions(r.version, installed) >= 0) : [];
  const older = installed ? releases.filter((r) => compareVersions(r.version, installed) < 0) : [...releases];
  // Nothing at or above the installed version (one built from source, or not known): the newest is open.
  if (open.length === 0) return { open: older.slice(0, 1), folded: older.slice(1, 1 + OLDER_VERSIONS_SHOWN) };
  return { open, folded: older.slice(0, OLDER_VERSIONS_SHOWN) };
}

/** "Plan pages" from "plan pages"; a title that opens with an identifier (fleet_page_show) keeps its case. */
function sentenceCase(title: string): string {
  const first = title.split(/\s/, 1)[0];
  if (/[_./]/.test(first)) return title;
  return title.charAt(0).toUpperCase() + title.slice(1);
}
