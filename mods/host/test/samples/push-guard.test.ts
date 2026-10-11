import { describe, expect, test } from "bun:test";
import * as mod from "../../../samples/push-guard/mod";
import { checked } from "./render";

/** Runs the mod's one hook the way the host would, with `branch` as the session's branch. */
async function ask(title: string, branch: string | null) {
  let matcher: any;
  let hook: any;
  mod.register(((_event: string, m: unknown, fn: unknown) => ((matcher = m), (hook = fn))) as never, {});
  const e = { sessionId: "ses_1", requestId: "per_1", kind: "shell", tool: "bash", title, patterns: [title] };
  // The host only calls the hook when the matcher passes.
  if (e.kind !== matcher.kind || !matcher.title.test(title)) return { matched: false, out: undefined };
  return { matched: true, out: await hook({ session: { branch: async () => branch } }, e, async () => ({ decision: "ask" })) };
}

describe("the push-guard sample", () => {
  test("passes the static check and hooks permission.ask", async () => {
    const report = await checked("push-guard");
    expect(report.hooks).toHaveLength(1);
    expect(report.hooks[0]).toMatchObject({ event: "permission.ask" });
  });

  test("a git push on main is denied with a reason", async () => {
    for (const title of ["git push", "git push origin main", "cd app && git  push -f"]) {
      const { out } = await ask(title, "main");
      expect(out).toEqual({ decision: "deny", reason: "Don't push from main. Make a branch and push that instead." });
    }
  });

  test("a push on another branch, a null branch and other commands are left to the user", async () => {
    expect((await ask("git push", "feature/berths")).out).toEqual({ decision: "ask" });
    expect((await ask("git push", null)).out).toEqual({ decision: "ask" });
    expect((await ask("git pushd", "main")).matched).toBe(false);
    expect((await ask("git status", "main")).matched).toBe(false);
  });
});
