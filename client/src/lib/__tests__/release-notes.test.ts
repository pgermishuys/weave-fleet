import { describe, expect, it } from "vitest";
import { compareVersions, parseReleaseNotes, releaseNotesMarkdown, whatsNewRange, type ReleaseNote } from "@/lib/release-notes";

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

function release(version: string): ReleaseNote {
  return { version, publishedAt: "2026-10-07T08:06:17Z", body: "", url: `https://example.test/v${version}` };
}

describe("parseReleaseNotes", () => {
  it("keeps what was written above the change list and groups the pull requests into new and fixed", () => {
    const notes = parseReleaseNotes(V044);

    expect(notes.intro).toBe(
      "## Fleet on your phone\n\nPair a phone from **Settings → Machines → Add a phone**.\n\n- **Known issue:** the `fleet` launcher doesn't accept `--require-token` yet.",
    );
    expect(notes.added).toEqual([
      { title: "Fleet on your phone: pairing, notifications", pr: { number: 377, url: "https://github.com/pgermishuys/weave-fleet/pull/377" } },
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
    expect(parseReleaseNotes("Release v0.1.0")).toEqual({ intro: "Release v0.1.0", added: [], fixed: [] });
  });
});

describe("releaseNotesMarkdown", () => {
  it("puts the intro's headings under the version and lists changes with their pull request", () => {
    const markdown = releaseNotesMarkdown(parseReleaseNotes(V044));

    expect(markdown).toContain("### Fleet on your phone");
    expect(markdown).toContain("#### New\n\n- Fleet on your phone: pairing, notifications [#377](https://github.com/pgermishuys/weave-fleet/pull/377)");
    expect(markdown).toContain("#### Fixed\n\n- The UX review's Fix now items (1–10) [#375]");
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
  const releases = ["0.46.0", "0.45.0", "0.44.0", "0.43.0", "0.42.0", "0.41.0", "0.40.0", "0.39.0", "0.38.0"].map(release);

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
