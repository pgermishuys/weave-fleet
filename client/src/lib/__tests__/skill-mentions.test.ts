import { beforeEach, describe, expect, it } from "vitest";
import {
  rememberSessionSkills,
  resetSessionSkills,
  sessionSkillNames,
  sessionSkills,
  skillDeliveries,
  skillNamesIn,
  skillTrigger,
  withSkillChips,
} from "@/lib/skill-mentions";
import { splitDraftReferences } from "@/lib/composer-references";

const skills = new Set(["fleet-debug", "fleet-simplify"]);

describe("skillTrigger", () => {
  it("opens on a $ at the start of a word, anywhere in the draft", () => {
    expect(skillTrigger("$", 1)).toEqual({ start: 0, query: "" });
    expect(skillTrigger("Then tidy it with $fleet-s", 26)).toEqual({ start: 18, query: "fleet-s" });
  });

  it("doesn't open inside a word, after a space, or inside backticks", () => {
    expect(skillTrigger("US$5", 4)).toBeNull();
    expect(skillTrigger("$fleet-debug now", 16)).toBeNull();
    expect(skillTrigger("run `echo $HOME", 15)).toBeNull();
    expect(skillTrigger("run `echo` $fl", 14)).toEqual({ start: 11, query: "fl" });
  });
});

describe("skillNamesIn", () => {
  it("finds the session's skills once each, in order, with sentence punctuation after them", () => {
    expect(skillNamesIn("Use $fleet-simplify, then $fleet-debug. Again: $fleet-simplify", skills))
      .toEqual(["fleet-simplify", "fleet-debug"]);
  });

  it("leaves unknown names, longer tokens and code alone", () => {
    expect(skillNamesIn("Why is $HOME empty? $5, $fleet-debug-2, `$fleet-debug`, a$fleet-debug", skills)).toEqual([]);
  });
});

describe("skillDeliveries", () => {
  it("attaches every skill on OpenCode 2, only the first on Claude Code, and notes them elsewhere", () => {
    const names = ["fleet-debug", "fleet-simplify"];
    expect(skillDeliveries("opencode2", names).map((skill) => skill.delivery)).toEqual(["attached", "attached"]);
    expect(skillDeliveries("claude-code", names).map((skill) => skill.delivery)).toEqual(["attached", "note"]);
    expect(skillDeliveries("opencode", names).map((skill) => skill.delivery)).toEqual(["note", "note"]);
  });
});

describe("session skills", () => {
  beforeEach(() => resetSessionSkills());

  it("keeps the skills among a session's commands, and those the harness can't tell apart", () => {
    rememberSessionSkills("s1", [
      { name: "fleet-debug", kind: "skill" },
      { name: "review", kind: "command" },
      { name: "deploy", userOnly: true },
    ]);
    expect([...sessionSkillNames("s1")]).toEqual(["fleet-debug", "deploy"]);
    expect(sessionSkills("s1")[1]).toEqual({ name: "deploy", description: undefined, userOnly: true });
    expect(sessionSkills("s2")).toEqual([]);
  });
});

describe("draft tint", () => {
  it("tints known skills by how they're sent and leaves other $ text plain", () => {
    const deliveries = new Map([["fleet-debug", "attached" as const], ["fleet-simplify", "note" as const]]);
    expect(splitDraftReferences("Use $fleet-debug, then $fleet-simplify on $HOME", null, new Set(), new Set(), deliveries)).toEqual([
      { text: "Use " },
      { text: "$fleet-debug", reference: "skill" },
      { text: ", then " },
      { text: "$fleet-simplify", reference: "skill", note: true },
      { text: " on $HOME" },
    ]);
  });

  it("keeps a skill the caret is still typing plain", () => {
    const deliveries = new Map([["fleet-debug", "attached" as const]]);
    expect(splitDraftReferences("Use $fleet-debug", 16, new Set(), new Set(), deliveries)).toEqual([{ text: "Use $fleet-debug" }]);
  });
});

describe("withSkillChips", () => {
  it("draws each skill as a chip, a note in its own tint, and leaves code alone", () => {
    const html = withSkillChips(
      "<p>Use $fleet-debug, then $fleet-simplify. <code>$fleet-debug</code> $fleet-debug-2</p>",
      [{ name: "fleet-debug", delivery: "attached" }, { name: "fleet-simplify", delivery: "note" }],
    );
    expect(html.match(/class="skill-ref-chip(?: [^"]*)?"/g)).toEqual(['class="skill-ref-chip"', 'class="skill-ref-chip skill-ref-chip--note"']);
    expect(html).toContain("<code>$fleet-debug</code> $fleet-debug-2</p>");
    expect(html).toContain('<span class="skill-ref-chip__name">fleet-simplify</span></span>. <code>');
  });
});
