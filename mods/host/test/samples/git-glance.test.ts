import { describe, expect, test } from "bun:test";
import * as mod from "../../../samples/git-glance/mod";
import { checked, fixture, pills, render } from "./render";

describe("the git-glance sample", () => {
  test("passes the static check and hooks the row line of shell tools", async () => {
    const report = await checked("git-glance");
    expect(report.hooks).toEqual([{ event: "ui.render", matcher: { component: "ToolUse", props: { tool: ["bash", "shell"] } } }]);
  });

  test("git status, long and short, say the same (real output)", () => {
    const expected = { staged: 1, modified: 3, untracked: 2 };
    expect(mod.parseStatus(fixture("git-status-long.txt"))).toEqual({ branch: "main", ...expected });
    expect(mod.parseStatus(fixture("git-status-short.txt"))).toEqual({ branch: "main", ...expected });
    expect(mod.parseStatus(fixture("git-status-short-nobranch.txt"))).toEqual(expected);
  });

  test("a clean tree and non-status output", () => {
    expect(mod.parseStatus("On branch main\nnothing to commit, working tree clean\n")).toEqual({ branch: "main", staged: 0, modified: 0, untracked: 0 });
    expect(mod.parseStatus("total 8\nfoo")).toBeUndefined();
  });

  test("git log counts the commits (real output)", () => {
    expect(mod.countCommits(fixture("git-log-oneline.txt"))).toBe(12);
    expect(mod.countCommits(fixture("git-log-full.txt"))).toBe(2);
    expect(mod.countCommits("")).toBeUndefined();
  });

  test("the row line", async () => {
    const status = await render(mod, "ToolUse", { input: { command: "git status" }, output: fixture("git-status-long.txt") });
    expect(pills(status)).toEqual(["1 staged", "3 modified", "2 untracked"]);
    const log = await render(mod, "ToolUse", { input: { command: "git log --oneline" }, output: fixture("git-log-oneline.txt") });
    expect(pills(log)).toEqual(["12 commits"]);
    expect(pills(await render(mod, "ToolUse", { input: { command: "git status -s" }, output: "" }))).toEqual([]);
    expect(await render(mod, "ToolUse", { input: { command: "git diff" }, output: "diff" })).toBe("next");
  });
});
