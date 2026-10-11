import { describe, expect, test } from "bun:test";
import * as mod from "../../../samples/branch-context/mod";
import { checked } from "./render";

/** Runs the mod's one hook the way the host would, with `branch` as the session's branch. */
async function submit(text: string, branch: string | null) {
  let matcher: any;
  let hook: any;
  mod.register(((_event: string, m: unknown, fn: unknown) => ((matcher = m), (hook = fn))) as never, {});
  const e = { sessionId: "ses_1", text, context: [] as string[] };
  // The host only calls the hook when the matcher passes.
  if (!matcher.text.test(text)) return e;
  return hook({ session: { branch: async () => branch } }, e, async (e2: unknown) => e2);
}

describe("the branch-context sample", () => {
  test("passes the static check and hooks prompt.submit on branch words", async () => {
    const report = await checked("branch-context");
    expect(report.hooks).toHaveLength(1);
    expect(report.hooks[0]).toMatchObject({ event: "prompt.submit" });
  });

  test("a prompt about branches, PRs, pushes or merges gets the branch as model-only context", async () => {
    for (const text of ["which branch am I on?", "Open a PR for this", "can you push it", "is it safe to MERGE?"]) {
      const out = await submit(text, "feature/berths");
      expect(out.text).toBe(text);
      expect(out.context).toEqual(["The session's current git branch is feature/berths."]);
    }
  });

  test("other prompts, and a folder with no branch, are left alone", async () => {
    expect((await submit("rename the helper", "feature/berths")).context).toEqual([]);
    expect((await submit("which branch am I on?", null)).context).toEqual([]);
  });
});
