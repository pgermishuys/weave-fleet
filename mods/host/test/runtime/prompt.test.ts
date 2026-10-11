import { describe, expect, test } from "bun:test";
import { setup } from "../helpers/harness";

const submit = { sessionId: "ses_test1", text: "which branch am I on?", context: [] as string[] };
const ids = (...names: string[]) => names.map((n) => `${n}@v1`);

describe("prompt.submit", () => {
  test("the end of the chain returns the event as it arrived, and a hook can leave it alone", async () => {
    const s = setup();
    expect((await s.dispatch("prompt.submit", [], submit)).result).toEqual(submit);
    await s.load("test-pass", `on("prompt.submit", ($, e, next) => next(e));`);
    const r = await s.dispatch("prompt.submit", ids("test-pass"), submit);
    expect(r).toMatchObject({ result: submit, failures: [] });
  });

  test("a rewrite passes along the chain, and the matcher still sees the original text", async () => {
    const s = setup();
    await s.load("test-a", `on("prompt.submit", ($, e, next) => next({ ...e, text: e.text.toUpperCase() }));`);
    await s.load("test-b", `on("prompt.submit", { text: /branch/ }, ($, e, next) => next({ ...e, text: e.text + " (b saw " + e.text.slice(0, 5) + ")" }));`);
    const r = await s.dispatch("prompt.submit", ids("test-a", "test-b"), submit);
    expect(r.result).toEqual({ ...submit, text: "WHICH BRANCH AM I ON? (b saw WHICH)", mod: "test-a" });
  });

  test("context accumulates across two mods", async () => {
    const s = setup();
    await s.load("test-a", `on("prompt.submit", ($, e, next) => next({ ...e, context: [...e.context, "from a"] }));`);
    await s.load("test-b", `on("prompt.submit", ($, e, next) => next({ ...e, context: [...e.context, "from b (saw " + e.context.length + ")"] }));`);
    const r = await s.dispatch("prompt.submit", ids("test-a", "test-b"), submit);
    expect(r.result).toEqual({ ...submit, context: ["from a", "from b (saw 1)"] });
  });

  test("a throwing hook leaves the event unchanged, strikes, and the chain goes on", async () => {
    const s = setup();
    await s.load("test-bad", `on("prompt.submit", () => { throw new Error("boom"); });`);
    await s.load("test-good", `on("prompt.submit", ($, e, next) => next({ ...e, context: ["still ran"] }));`);
    const r = await s.dispatch("prompt.submit", ids("test-bad", "test-good"), submit);
    expect(r.result).toEqual({ ...submit, context: ["still ran"] });
    expect(r.failures).toMatchObject([{ mod: "test-bad@v1", event: "prompt.submit", kind: "throw", message: "boom", strikes: 1 }]);
  });

  test("an answer without a string text, or with a context that is not strings, is a failure", async () => {
    const s = setup();
    await s.load("test-notext", `on("prompt.submit", ($, e, next) => ({ context: [] }));`);
    await s.load("test-badctx", `on("prompt.submit", ($, e, next) => ({ ...e, context: [1, 2] }));`);
    await s.load("test-nothing", `on("prompt.submit", () => undefined);`);
    for (const name of ["test-notext", "test-badctx", "test-nothing"]) {
      const r = await s.dispatch("prompt.submit", ids(name), submit);
      expect(r.result).toEqual(submit);
      expect(r.failures).toMatchObject([{ mod: `${name}@v1`, kind: "throw", strikes: 1 }]);
    }
  });

  test(".catch answers a failed hook", async () => {
    const s = setup();
    await s.load("test-rescued", `on("prompt.submit", () => { throw new Error("x"); }).catch(($, e, next) => next({ ...e, text: "rescued" }));`);
    const r = await s.dispatch("prompt.submit", ids("test-rescued"), submit);
    expect(r).toMatchObject({ result: { ...submit, text: "rescued" }, failures: [] });
  });

  test("a prompt hook has promptHookMs, not hookMs", async () => {
    const s = setup({ hookMs: 5_000, promptHookMs: 60 });
    await s.load("test-slow", `on("prompt.submit", async ($, e, next) => { await new Promise((r) => $.clock.after(300, r)); return next({ ...e, text: "late" }); });`);
    const r = await s.dispatch("prompt.submit", ids("test-slow"), submit);
    expect(r.result).toEqual(submit);
    expect(r.failures).toMatchObject([{ mod: "test-slow@v1", kind: "timeout", strikes: 1 }]);
  });

  test("$.session.branch() asks Fleet and answers null when it has none", async () => {
    const s = setup();
    s.peer.answers["session.get"] = (p: any) => ({ id: p.sessionId, title: "t", harness: "opencode", cwd: "/work/demo", surfaces: [], branch: p.sessionId === "ses_test1" ? "feature/berths" : null });
    await s.load("test-branch", `on("prompt.submit", async ($, e, next) => next({ ...e, context: [String(await $.session.branch())] }));`);
    expect((await s.dispatch("prompt.submit", ids("test-branch"), submit)).result.context).toEqual(["feature/berths"]);
    const other = { ...submit, sessionId: "ses_other" };
    expect((await s.dispatch("prompt.submit", ids("test-branch"), other, "ses_other")).result.context).toEqual(["null"]);
  });

  test("a rewrite names the mod that changed the text; a pass-through or a context-only hook does not", async () => {
    const s = setup();
    await s.load("test-inner", `on("prompt.submit", ($, e, next) => next({ ...e, text: "Looks good to me." }));`);
    await s.load("test-outer", `on("prompt.submit", async ($, e, next) => next(e));`);
    await s.load("test-notes", `on("prompt.submit", ($, e, next) => next({ ...e, context: ["a note"] }));`);
    expect((await s.dispatch("prompt.submit", ids("test-outer", "test-inner"), submit)).result.mod).toBe("test-inner");
    const notesOnly = (await s.dispatch("prompt.submit", ids("test-notes"), submit)).result;
    expect(notesOnly.mod).toBeUndefined();
  });
});
