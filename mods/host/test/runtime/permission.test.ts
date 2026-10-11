import { describe, expect, test } from "bun:test";
import { setup } from "../helpers/harness";

const ask = { sessionId: "ses_test1", requestId: "per_1", kind: "shell", tool: "bash", title: "git push origin main", patterns: ["git push origin main"] };
const ids = (...names: string[]) => names.map((n) => `${n}@v1`);

describe("permission.ask", () => {
  test("the end of the chain is { decision: 'ask' }, and a hook can leave the ask alone", async () => {
    const s = setup();
    expect((await s.dispatch("permission.ask", [], ask)).result).toEqual({ decision: "ask" });
    await s.load("test-pass", `on("permission.ask", ($, e, next) => next(e));`);
    expect(await s.dispatch("permission.ask", ids("test-pass"), ask)).toMatchObject({ result: { decision: "ask" }, failures: [] });
  });

  test("a deny passes through, names its mod, and later mods do not run", async () => {
    const s = setup();
    await s.load("test-guard", `on("permission.ask", { kind: "shell", title: /git push/ }, () => ({ decision: "deny", reason: "no pushing" }));`);
    await s.load("test-later", `on("permission.ask", ($, e, next) => { $.ui.log("ran"); return next(e); });`);
    const r = await s.dispatch("permission.ask", ids("test-guard", "test-later"), ask);
    expect(r).toMatchObject({ result: { decision: "deny", reason: "no pushing", mod: "test-guard" }, failures: [] });
    const other = await s.dispatch("permission.ask", ids("test-guard"), { ...ask, title: "git status" });
    expect(other.result).toEqual({ decision: "ask" });
  });

  test("a deny behind next() still carries the mod that made it", async () => {
    const s = setup();
    await s.load("test-outer", `on("permission.ask", ($, e, next) => next(e));`);
    await s.load("test-inner", `on("permission.ask", () => ({ decision: "deny", reason: "inner says no" }));`);
    const r = await s.dispatch("permission.ask", ids("test-outer", "test-inner"), ask);
    expect(r.result).toMatchObject({ decision: "deny", reason: "inner says no", mod: "test-inner" });
  });

  test("a deny without a reason, with a long one, or an unknown decision is a failure", async () => {
    const s = setup();
    await s.load("test-noreason", `on("permission.ask", () => ({ decision: "deny" }));`);
    await s.load("test-blank", `on("permission.ask", () => ({ decision: "deny", reason: "  " }));`);
    await s.load("test-long", `on("permission.ask", () => ({ decision: "deny", reason: "x".repeat(501) }));`);
    await s.load("test-allow", `on("permission.ask", () => ({ decision: "allow" }));`);
    for (const name of ["test-noreason", "test-blank", "test-long", "test-allow"]) {
      const r = await s.dispatch("permission.ask", ids(name), ask);
      expect(r.result).toEqual({ decision: "ask" });
      expect(r.failures).toMatchObject([{ mod: `${name}@v1`, event: "permission.ask", kind: "throw", strikes: 1 }]);
    }
  });

  test("a throwing hook strikes and the chain goes on to the next mod", async () => {
    const s = setup();
    await s.load("test-bad", `on("permission.ask", () => { throw new Error("boom"); });`);
    await s.load("test-good", `on("permission.ask", () => ({ decision: "deny", reason: "still ran" }));`);
    const r = await s.dispatch("permission.ask", ids("test-bad", "test-good"), ask);
    expect(r.result).toMatchObject({ decision: "deny", reason: "still ran" });
    expect(r.failures).toMatchObject([{ mod: "test-bad@v1", kind: "throw", message: "boom", strikes: 1 }]);
  });

  test("a permission hook has promptHookMs, not hookMs", async () => {
    const s = setup({ hookMs: 5_000, promptHookMs: 60 });
    await s.load("test-slow", `on("permission.ask", async ($, e, next) => { await new Promise((r) => $.clock.after(300, r)); return { decision: "deny", reason: "late" }; });`);
    const r = await s.dispatch("permission.ask", ids("test-slow"), ask);
    expect(r.result).toEqual({ decision: "ask" });
    expect(r.failures).toMatchObject([{ mod: "test-slow@v1", kind: "timeout", strikes: 1 }]);
  });
});
