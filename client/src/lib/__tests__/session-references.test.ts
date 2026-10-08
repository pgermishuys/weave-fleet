import { beforeEach, describe, expect, it } from "vitest";
import type { SessionListItem } from "@/api/client";
import {
  matchReferableSessions,
  parseSessionReferences,
  rememberedSessionTokens,
  rememberSessionReference,
  resetSessionReferences,
  sessionReferenceSlug,
  sessionReferencesIn,
  sessionReferenceToken,
  stripSessionReferences,
  textHasToken,
  withSessionReferenceChips,
} from "@/lib/session-references";

const HOUR = 60 * 60_000;
const NOW = Date.parse("2026-10-04T12:00:00Z");

function item(id: string, title: string, overrides: Partial<SessionListItem> = {}): SessionListItem {
  return {
    instanceId: "inst",
    workspaceId: "ws",
    workspaceDirectory: "/work/weave-fleet",
    workspaceDisplayName: null,
    isolationStrategy: "existing",
    sessionStatus: "idle",
    session: { id, title, time: { created: NOW - 10 * HOUR, updated: NOW - HOUR } },
    instanceStatus: "running",
    lifecycleStatus: "running",
    retentionStatus: "active",
    typedInstanceStatus: "running",
    isHidden: false,
    harnessType: "opencode2",
    tags: [],
    ...overrides,
  } as SessionListItem;
}

describe("session reference tokens", () => {
  beforeEach(() => {
    localStorage.clear();
    resetSessionReferences();
  });

  it("makes a token from the title", () => {
    expect(sessionReferenceSlug("t3code: what can we learn?")).toBe("t3code-what-can-we-learn");
    expect(sessionReferenceSlug("Port t3code's limit recovery")).toBe("port-t3code-s-limit-recovery");
    expect(sessionReferenceSlug("Café résumé")).toBe("cafe-resume");
    expect(sessionReferenceSlug("???")).toBe("session");
    expect(sessionReferenceSlug("a very long title that keeps going well past forty characters").length).toBeLessThanOrEqual(40);
  });

  it("gives a second session with the same title its own token, and a session picked again its old one", () => {
    const first = sessionReferenceToken("here", { id: "s1", title: "Notes" });
    rememberSessionReference("here", { token: first, sessionId: "s1", title: "Notes" });

    expect(first).toBe("@notes");
    expect(sessionReferenceToken("here", { id: "s2", title: "Notes" })).toBe("@notes-2");
    expect(sessionReferenceToken("here", { id: "s1", title: "Notes" })).toBe("@notes");
  });

  it("sends only the picks still in the text, in the order they appear", () => {
    rememberSessionReference("here", { token: "@notes", sessionId: "s1", title: "Notes" });
    rememberSessionReference("here", { token: "@plan", sessionId: "s2", title: "Plan" });
    rememberSessionReference("here", { token: "@gone", sessionId: "s3", title: "Gone" });

    expect(sessionReferencesIn("here", "Use @plan, then @notes.").map((ref) => ref.sessionId)).toEqual(["s2", "s1"]);
    expect(sessionReferencesIn("elsewhere", "Use @plan")).toEqual([]);
  });

  it("keeps the picks with the draft across a reload", () => {
    rememberSessionReference("here", { token: "@notes", sessionId: "s1", title: "Notes" });
    resetSessionReferences();

    expect(sessionReferencesIn("here", "see @notes")).toEqual([{ token: "@notes", sessionId: "s1", title: "Notes" }]);
    expect(rememberedSessionTokens("here").has("@notes")).toBe(true);
  });

  it("finds a token only where it stands on its own", () => {
    expect(textHasToken("from @notes.", "@notes")).toBe(true);
    expect(textHasToken("(@notes)", "@notes")).toBe(true);
    expect(textHasToken("@notes-2", "@notes")).toBe(false);
    expect(textHasToken("@notes.md", "@notes")).toBe(false);
    expect(textHasToken("me@notes", "@notes")).toBe(false);
  });
});

describe("the block in a sent message", () => {
  const sent = [
    "Use the mapping from @t3code-what-can-we-learn",
    "",
    "<fleet-session-references>",
    "Fleet sessions the user referenced with @ in the message above. They are context…",
    "<session ref=\"@t3code-what-can-we-learn\" id=\"ses-1\" title=\"t3code: what &amp; how?\" />",
    "<session ref=\"@notes\" id=\"ses-2\" title=\"Notes\">",
    "It &lt;mapped&gt; subagents.",
    "</session>",
    "</fleet-session-references>",
  ].join("\n");

  it("splits what was typed from the sessions the block names", () => {
    expect(parseSessionReferences(sent)).toEqual({
      text: "Use the mapping from @t3code-what-can-we-learn",
      references: [
        { token: "@t3code-what-can-we-learn", sessionId: "ses-1", title: "t3code: what & how?" },
        { token: "@notes", sessionId: "ses-2", title: "Notes" },
      ],
    });
    expect(stripSessionReferences(sent)).toBe("Use the mapping from @t3code-what-can-we-learn");
  });

  it("leaves a message without a block as it is", () => {
    expect(parseSessionReferences("Hello <fleet-session-references> in prose")).toEqual({
      text: "Hello <fleet-session-references> in prose",
      references: [],
    });
  });
});

describe("chips in a rendered message", () => {
  const references = [{ token: "@notes", sessionId: "ses-1", title: "Notes <draft>" }];

  it("draws each token as a chip linking to its session", () => {
    const html = withSessionReferenceChips("<p>Read @notes, then go.</p>", references);

    expect(html).toContain("<a class=\"session-ref-chip\" href=\"/sessions/ses-1\" data-session-ref=\"ses-1\"");
    expect(html).toContain("<span class=\"session-ref-chip__title\">Notes &lt;draft&gt;</span></a>, then go.");
    expect(html).not.toContain("@notes");
  });

  it("leaves tokens in code alone", () => {
    expect(withSessionReferenceChips("<p><code>@notes</code></p>", references)).toBe("<p><code>@notes</code></p>");
  });
});

describe("the sessions the @ list offers", () => {
  const items = [
    item("old", "Port t3code's limit recovery", {
      retentionStatus: "archived",
      workspaceDirectory: "/work/weave-fleet",
      session: { id: "old", title: "Port t3code's limit recovery", time: { created: NOW - 80 * HOUR, updated: NOW - 72 * HOUR } },
    } as Partial<SessionListItem>),
    item("parent", "t3code: what can we learn?", { harnessType: "claude-code", projectName: "tidytempo" }),
    item("child", "Survey t3code features", { parentSessionId: "parent", isHidden: true }),
    item("here", "Capture subagents with t3code"),
    item("other", "Fleet MCP server spike"),
  ];

  it("ranks titles that start with the text, then words that do, then the rest; newest first within each", () => {
    const names: Record<string, string> = { "claude-code": "Claude Code", opencode2: "OpenCode 2" };
    const offered = matchReferableSessions(items, "t3c", { excludeId: "here", limit: 5, harnessName: (type) => names[type] });

    expect(offered.map((session) => session.id)).toEqual(["parent", "child", "old"]);
    expect(offered.map((session) => session.description)).toEqual([
      "tidytempo · Claude Code",
      "subagent of t3code: what can we learn?",
      "weave-fleet · OpenCode 2 · archived",
    ]);
  });

  it("offers the newest for a bare @, never the session being written to", () => {
    const offered = matchReferableSessions(items, "", { excludeId: "here", limit: 3 });

    expect(offered).toHaveLength(3);
    expect(offered.map((session) => session.id)).not.toContain("here");
  });

  it("names a harness the way the caller does", () => {
    const offered = matchReferableSessions(items, "fleet", { limit: 5, harnessName: (type) => `<${type}>` });

    expect(offered[0].description).toBe("weave-fleet · <opencode2>");
  });

  it("names a harness the caller doesn't know by its type, without guessing a name", () => {
    const offered = matchReferableSessions(items, "fleet", { limit: 5, harnessName: () => undefined });

    expect(offered[0].description).toBe("weave-fleet · opencode2");
  });
});
