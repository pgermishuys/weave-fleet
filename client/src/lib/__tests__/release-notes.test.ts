import { describe, expect, it } from "vitest";
import {
  changeSummary,
  compareVersions,
  introHighlight,
  parseReleaseNotes,
  releaseNotesMarkdown,
  whatsNewRange,
  whatsNewSummary,
  type ReleaseNote,
} from "@/lib/release-notes";

// v0.44.0's notes as the fleet-releases mirror has them: written highlights, GitHub's list, the link back.
const V044 = `## Fleet on your phone

Pair a phone from **Settings → Machines → Add a phone**.

- **Known issue:** the \`fleet\` launcher doesn't accept \`--require-token\` yet.

## What's Changed
* fix(ui): the UX review's Fix now items (1–10) by @pgermishuys in https://github.com/pgermishuys/weave-fleet/pull/375
* feat(phone): Fleet on your phone: pairing, notifications by @pgermishuys in https://github.com/pgermishuys/weave-fleet/pull/377
* Pin sessions above the projects by @pgermishuys in https://github.com/pgermishuys/weave-fleet/pull/386
* fleet_page_show checks the page by @pgermishuys in https://github.com/pgermishuys/weave-fleet/pull/388

## New Contributors
* @someone made their first contribution in https://github.com/pgermishuys/weave-fleet/pull/370

**Full Changelog**: https://github.com/pgermishuys/weave-fleet/compare/v0.43.0...v0.44.0

---
Release notes from https://github.com/pgermishuys/weave-fleet/releases/tag/v0.44.0
`;

// A release the way GitHub generates them: no written intro, tests and refactors in among the changes.
const V049 = `## What's Changed
* test(preview): hold the stranger port so the app can't be given it by @pgermishuys in https://github.com/pgermishuys/weave-fleet/pull/429
* fix(analytics): open on a range that ends today by @pgermishuys in https://github.com/pgermishuys/weave-fleet/pull/430
* fix(files): show images in a file tab as a picture, not in the editor by @pgermishuys in https://github.com/pgermishuys/weave-fleet/pull/431
* fix(sessions): line up a lone session's title with a parent's under a project by @pgermishuys in https://github.com/pgermishuys/weave-fleet/pull/432
* feat(conversation): open files a reply names in a tab, at the line by @pgermishuys in https://github.com/pgermishuys/weave-fleet/pull/434
* fix(claude-code): keep what the agent left running past an idle stop by @pgermishuys in https://github.com/pgermishuys/weave-fleet/pull/433
* refactor(client): one event hub connection per machine by @pgermishuys in https://github.com/pgermishuys/weave-fleet/pull/414
* docs: Fleet nodes, one interface for many machines by @pgermishuys in https://github.com/pgermishuys/weave-fleet/pull/401


**Full Changelog**: https://github.com/pgermishuys/weave-fleet/compare/v0.48.0...v0.49.0`;

function release(version: string, body = ""): ReleaseNote {
  return { version, publishedAt: "2026-10-07T08:06:17Z", body, url: `https://example.test/v${version}` };
}

function changes(lines: string[]): string {
  return `## What's Changed\n${lines.map((line, i) => `* ${line} by @someone in https://github.com/o/r/pull/${i + 1}`).join("\n")}`;
}

describe("parseReleaseNotes", () => {
  it("keeps what was written above the change list and groups the pull requests into new and fixed", () => {
    const notes = parseReleaseNotes(V044);

    expect(notes.intro).toBe(
      "## Fleet on your phone\n\nPair a phone from **Settings → Machines → Add a phone**.\n\n- **Known issue:** the `fleet` launcher doesn't accept `--require-token` yet.",
    );
    expect(notes.added).toEqual([
      { title: "Fleet on your phone: pairing, notifications", scope: "Phone", pr: { number: 377, url: "https://github.com/pgermishuys/weave-fleet/pull/377" } },
      { title: "Pin sessions above the projects", pr: { number: 386, url: "https://github.com/pgermishuys/weave-fleet/pull/386" } },
      { title: "fleet_page_show checks the page", pr: { number: 388, url: "https://github.com/pgermishuys/weave-fleet/pull/388" } },
    ]);
    expect(notes.fixed).toEqual([
      { title: "The UX review's Fix now items (1–10)", pr: { number: 375, url: "https://github.com/pgermishuys/weave-fleet/pull/375" } },
    ]);
  });

  it("leaves out the full changelog, new contributors and the mirror's link back", () => {
    const markdown = releaseNotesMarkdown(parseReleaseNotes(V044));

    expect(markdown).not.toContain("Full Changelog");
    expect(markdown).not.toContain("first contribution");
    expect(markdown).not.toContain("Release notes from");
  });

  it("reads a release with nothing but a placeholder as an intro", () => {
    expect(parseReleaseNotes("Release v0.1.0")).toEqual({ intro: "Release v0.1.0", added: [], fixed: [], internal: 0 });
  });

  it("counts tests, refactors and docs instead of listing them as new", () => {
    const notes = parseReleaseNotes(V049);

    expect(notes.added.map((c) => c.title)).toEqual(["Open files a reply names in a tab, at the line"]);
    expect(notes.fixed).toHaveLength(4);
    expect(notes.internal).toBe(3);
    expect(JSON.stringify(notes)).not.toContain("stranger port");
  });

  it("keeps the scope as a label, but not one that only names a layer of Fleet", () => {
    const notes = parseReleaseNotes(
      changes(["fix(claude-code): a", "feat(status-bar): b", "feat(client): c", "fix(opencode2): d", "Pin sessions above the projects"]),
    );

    expect([...notes.added, ...notes.fixed].map((c) => [c.title, c.scope])).toEqual([
      ["B", "Status bar"],
      ["C", undefined],
      ["Pin sessions above the projects", undefined],
      ["A", "Claude Code"],
      ["D", "OpenCode 2"],
    ]);
  });
});

describe("changeSummary", () => {
  it("counts what's new and what's fixed", () => {
    expect(changeSummary(parseReleaseNotes(V049))).toBe("1 new, 4 fixed");
    expect(changeSummary(parseReleaseNotes(changes(["fix: a"])))).toBe("1 fixed");
    expect(changeSummary(parseReleaseNotes(changes(["test: a"])))).toBe("");
  });
});

describe("introHighlight", () => {
  it("takes the first heading or sentence written by hand, as plain text", () => {
    expect(introHighlight(parseReleaseNotes(V044).intro)).toBe("Fleet on your phone");
    expect(introHighlight("Files a reply names open in a **tab**, see [the docs](https://x.test).")).toBe("Files a reply names open in a tab, see the docs.");
  });

  it("skips a known issue", () => {
    expect(introHighlight("**Known issue (from v0.44.0, still open):** the launcher.")).toBeNull();
    expect(introHighlight("")).toBeNull();
  });
});

describe("whatsNewSummary", () => {
  const v048 = release("0.48.0", changes(["feat(composer): pick a skill", "feat(folders): one box to open a folder", "fix: a reply so far", "test: wait"]));
  const v049 = release("0.49.0", V049);
  const v047 = release("0.47.1", changes(["fix(launcher): accepts --require-token"]));
  const releases = [v049, v048, v047];

  it("lists what's new before what's fixed, with the rest counted", () => {
    const summary = whatsNewSummary(releases, "0.48.0", "0.49.0");

    expect(summary?.releases.map((r) => r.version)).toEqual(["0.49.0"]);
    expect(summary?.items).toEqual([
      { kind: "new", text: "Open files a reply names in a tab, at the line", scope: "Conversation" },
      { kind: "fixed", text: "Open on a range that ends today", scope: "Analytics" },
      { kind: "fixed", text: "Show images in a file tab as a picture, not in the editor", scope: "Files" },
    ]);
    expect(summary?.more).toBe(2);
    expect(summary?.moreAreFixes).toBe(true);
  });

  it("puts what was written by hand first", () => {
    const written = release("0.49.0", `Files a reply names now open in a tab.\n\n${V049}`);

    expect(whatsNewSummary([written], "0.48.0", "0.49.0")?.items[0]).toEqual({ kind: "highlight", text: "Files a reply names now open in a tab." });
  });

  it("covers every version an update skipped over, newest first", () => {
    const summary = whatsNewSummary(releases, "0.47.1", "0.49.0");

    expect(summary?.releases.map((r) => r.version)).toEqual(["0.49.0", "0.48.0"]);
    expect(summary?.items.map((i) => i.text)).toEqual(["Open files a reply names in a tab, at the line", "Pick a skill", "One box to open a folder"]);
    expect(summary?.more).toBe(5);
    expect(summary?.moreAreFixes).toBe(true);
  });

  it("is null without the running version's notes, so the caller says less", () => {
    expect(whatsNewSummary([v048, v047], "0.48.0", "0.49.0")).toBeNull();
    expect(whatsNewSummary([], "0.48.0", "0.49.0")).toBeNull();
  });

  it("is null when the update has nothing but behind-the-scenes changes", () => {
    expect(whatsNewSummary([release("0.49.1", changes(["test: a", "ci: b"]))], "0.49.0", "0.49.1")).toBeNull();
  });
});

describe("releaseNotesMarkdown", () => {
  it("puts the intro's headings under the version and lists changes with their pull request", () => {
    const markdown = releaseNotesMarkdown(parseReleaseNotes(V044));

    expect(markdown).toContain("### Fleet on your phone");
    expect(markdown).toContain("#### New\n\n- *Phone* Fleet on your phone: pairing, notifications [#377](https://github.com/pgermishuys/weave-fleet/pull/377)");
    expect(markdown).toContain("#### Fixed\n\n- The UX review's Fix now items (1–10) [#375]");
  });

  it("puts a change's scope before it, for a quiet label", () => {
    expect(releaseNotesMarkdown(parseReleaseNotes(V049))).toContain("- *Analytics* Open on a range that ends today [#430]");
  });
});

describe("compareVersions", () => {
  it("compares numerically", () => {
    expect(compareVersions("0.10.0", "0.9.0")).toBeGreaterThan(0);
    expect(compareVersions("v0.46.0", "0.46.0")).toBe(0);
    expect(compareVersions("0.45.0", "0.46.0")).toBeLessThan(0);
  });
});

describe("whatsNewRange", () => {
  const releases = ["0.46.0", "0.45.0", "0.44.0", "0.43.0", "0.42.0", "0.41.0", "0.40.0", "0.39.0", "0.38.0"].map((version) => release(version));

  it("opens every version newer than the installed one and the installed one, and folds five older", () => {
    const range = whatsNewRange(releases, "0.44.0");

    expect(range.open.map((r) => r.version)).toEqual(["0.46.0", "0.45.0", "0.44.0"]);
    expect(range.folded.map((r) => r.version)).toEqual(["0.43.0", "0.42.0", "0.41.0", "0.40.0", "0.39.0"]);
  });

  it("opens the newest when the installed version isn't known", () => {
    const range = whatsNewRange(releases, null);

    expect(range.open.map((r) => r.version)).toEqual(["0.46.0"]);
    expect(range.folded).toHaveLength(5);
  });

  it("opens the newest for a build newer than every release (one built from source)", () => {
    const range = whatsNewRange(releases, "0.47.0");

    expect(range.open.map((r) => r.version)).toEqual(["0.46.0"]);
    expect(range.folded.map((r) => r.version)).toEqual(["0.45.0", "0.44.0", "0.43.0", "0.42.0", "0.41.0"]);
  });
});
