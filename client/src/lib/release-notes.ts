/** One Fleet release, as GET /api/update/releases lists it (newest first). */
export interface ReleaseNote {
  version: string;
  publishedAt: string | null;
  body: string;
  url: string;
}

export interface ReleaseChange {
  title: string;
  /** The pull request, when the line names one. */
  pr?: { number: number; url: string };
}

/** A release's notes split the way What's new shows them. */
export interface ParsedReleaseNotes {
  /** What's written above the change list: highlights, known issues. Markdown. */
  intro: string;
  added: ReleaseChange[];
  fixed: ReleaseChange[];
}

/** How many versions older than the installed one What's new keeps, folded. */
export const OLDER_VERSIONS_SHOWN = 5;

// GitHub's generated notes: "* <title> by @<user> in https://github.com/<owner>/<repo>/pull/<n>".
const CHANGE_LINE = /^\s*[*-]\s+(.+?)(?:\s+by\s+@[\w-]+(?:\[bot\])?)?\s+in\s+(https:\/\/github\.com\/[\w.-]+\/[\w.-]+\/pull\/(\d+))\s*$/;
const CONVENTIONAL_PREFIX = /^(\w+)(\([^)]*\))?!?:\s*/;
// The fleet-releases mirror adds a link back to the source release at the end.
const MIRROR_FOOTER = /\n-{3,}\s*\nRelease notes from https?:\/\/\S+\s*$/;

/**
 * Splits GitHub's release notes into what was written by hand (above "What's Changed") and the pull requests,
 * grouped into new and fixed by their conventional-commit prefix, which is dropped. The "Full Changelog" line
 * and the "New Contributors" section are left out.
 */
export function parseReleaseNotes(body: string): ParsedReleaseNotes {
  const lines = body.replace(/\r\n/g, "\n").replace(MIRROR_FOOTER, "").split("\n");
  const intro: string[] = [];
  const added: ReleaseChange[] = [];
  const fixed: ReleaseChange[] = [];
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
    const entry: ReleaseChange = { title: sentenceCase(prefix ? raw.slice(prefix[0].length) : raw) };
    if (change) entry.pr = { number: Number(change[3]), url: change[2] };
    (prefix?.[1].toLowerCase() === "fix" ? fixed : added).push(entry);
  }

  return { intro: intro.join("\n").trim(), added, fixed };
}

/** The notes as Markdown for Fleet's renderer: the intro, then New and Fixed lists with their pull requests. */
export function releaseNotesMarkdown(notes: ParsedReleaseNotes): string {
  const list = (label: string, changes: ReleaseChange[]) =>
    changes.length === 0
      ? ""
      : `#### ${label}\n\n${changes.map((c) => `- ${c.title}${c.pr ? ` [#${c.pr.number}](${c.pr.url})` : ""}`).join("\n")}\n`;
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
