import { describe, expect, test } from "bun:test";
import * as mod from "../../../samples/tests-after-edits/mod";
import { checked } from "./render";

/** Runs the mod's one hook the way the host would; `refuse` makes Fleet refuse the queued message. */
async function complete(over: Record<string, unknown>, refuse?: string) {
  let hook: any;
  mod.register(((_event: string, fn: unknown) => (hook = fn)) as never, {});
  const queued: string[] = [];
  const logged: string[] = [];
  const $ = {
    prompt: {
      submit: async ({ text }: { text: string }) => {
        if (refuse) throw new Error(refuse);
        queued.push(text);
      },
    },
    ui: { log: (text: string) => logged.push(text) },
  };
  const e = { sessionId: "ses_1", turnId: "msg_1", isAborted: false, isFailed: false, changedFiles: [] as string[], ...over };
  const out = await hook($, e, async (e2: unknown) => e2);
  return { out, e, queued, logged };
}

describe("the tests-after-edits sample", () => {
  test("passes the static check and hooks turn.complete", async () => {
    const report = await checked("tests-after-edits");
    expect(report.hooks).toHaveLength(1);
    expect(report.hooks[0]).toMatchObject({ event: "turn.complete" });
  });

  test("a turn that changed files queues Run the tests, and the event goes on unchanged", async () => {
    const r = await complete({ changedFiles: ["src/berths.ts"] });
    expect(r.queued).toEqual(["Run the tests"]);
    expect(r.out).toBe(r.e);
  });

  test("a turn that changed nothing, was stopped or failed queues nothing", async () => {
    expect((await complete({})).queued).toEqual([]);
    expect((await complete({ changedFiles: ["a.ts"], isAborted: true })).queued).toEqual([]);
    expect((await complete({ changedFiles: ["a.ts"], isFailed: true })).queued).toEqual([]);
  });

  test("a refusal is logged, not thrown", async () => {
    const r = await complete({ changedFiles: ["a.ts"] }, "queued 3 follow-ups in a row");
    expect(r.queued).toEqual([]);
    expect(r.logged).toEqual(["Could not queue the tests: queued 3 follow-ups in a row"]);
    expect(r.out).toBe(r.e);
  });
});
