import { describe, expect, test } from "bun:test";
import { renderE, setup, sleep } from "../helpers/harness";

const band = renderE("ComposerBand", "ses_test1", { isWorking: false });
const chip = renderE("StatusChip", "ses_test1", {});
const text = (s: string) => `$.ui.resolve(e).Text({ children: [${JSON.stringify(s)}] })`;
const logs = (s: ReturnType<typeof setup>) => s.peer.notes("log").map((l) => l.text);

describe("budget", () => {
  test("time awaiting a slow $ call isn't the hook's own time", async () => {
    const s = setup({ hookMs: 60 });
    s.peer.answers["store.get"] = async () => {
      await new Promise((r) => setTimeout(r, 150));
      return { value: "saved" };
    };
    await s.load("test-slow-store", `on("ui.render", async ($, e) => { const v = await $.store.get("k"); return $.ui.resolve(e).Text({ children: [v] }); });`);
    const r = await s.render(["test-slow-store@v1"], band);
    expect(r.failures).toEqual([]);
    expect(r.result.children).toEqual(["saved"]);
  });

  test("time awaiting next isn't the hook's own time", async () => {
    const s = setup({ hookMs: 60 });
    await s.load("test-outer", `on("ui.render", async ($, e, next) => next(e));`);
    await s.load("test-inner", `on("ui.render", async ($, e) => { await new Promise((r) => $.clock.after(150, r)); return ${text("inner")}; });`);
    // the inner hook uses 150 ms of its own time and times out; the outer one waited the same 150 ms and must not
    const r = await s.render(["test-outer@v1", "test-inner@v1"], band);
    expect(r.failures.map((f: any) => f.mod)).toEqual(["test-inner@v1"]);
    expect(r.result).toEqual({ type: "Fleet" });
  });

  test("a hook that spends its own time past the budget fails with timeout", async () => {
    const s = setup({ hookMs: 60 });
    await s.load(
      "test-burn",
      `on("ui.render", async ($, e) => {
         for (let i = 0; i < 20; i++) { const t = Date.now() + 10; while (Date.now() < t) {} await new Promise((r) => $.clock.after(0, r)); }
         return ${text("late")};
       });`,
    );
    const r = await s.render(["test-burn@v1"], band);
    expect(r.failures).toMatchObject([{ mod: "test-burn@v1", event: "ui.render", kind: "timeout", strikes: 1 }]);
    expect(r.result).toEqual({ type: "Fleet" });
  });

  test("another session's busy hook doesn't use up this hook's budget", async () => {
    const s = setup({ hookMs: 100 });
    await s.load("test-wait", `on("turn.complete", async ($, e, next) => { await new Promise((r) => $.clock.after(30, r)); await new Promise((r) => $.clock.after(30, r)); return next(e); });`);
    await s.load("test-busy", `on("turn.complete", ($, e, next) => { const t = Date.now() + 90; while (Date.now() < t) {} return next(e); });`);
    const tc = (sessionId: string) => ({ sessionId, turnId: "t", isAborted: false, isFailed: false });
    const a = s.dispatch("turn.complete", ["test-wait@v1"], tc("ses_A"), "ses_A");
    const b = s.dispatch("turn.complete", ["test-busy@v1"], tc("ses_B"), "ses_B");
    const [ra, rb] = await Promise.all([a, b]);
    expect(ra.failures).toEqual([]);
    expect(rb.failures).toEqual([]);
  });

  test("another mod's busy timer doesn't use up this hook's budget", async () => {
    const s = setup({ hookMs: 100 });
    await s.load("test-ticker", `on("session.start", ($, e, next) => { $.clock.after(10, () => { const t = Date.now() + 90; while (Date.now() < t) {} }); return next(e); });`);
    await s.load("test-wait", `on("turn.complete", async ($, e, next) => { await new Promise((r) => $.clock.after(30, r)); await new Promise((r) => $.clock.after(30, r)); return next(e); });`);
    const ticker = s.dispatch("session.start", ["test-ticker@v1"], { sessionId: "ses_B", reason: "start" }, "ses_B");
    const r = await s.dispatch("turn.complete", ["test-wait@v1"], { sessionId: "ses_A", turnId: "t", isAborted: false, isFailed: false }, "ses_A");
    await ticker;
    expect(r.failures).toEqual([]);
    expect(s.peer.notes("failed")).toEqual([]);
  });

  test("overlapping waits pause the budget once", async () => {
    const s = setup({ hookMs: 80 });
    s.peer.answers["store.get"] = async () => {
      await new Promise((r) => setTimeout(r, 120));
      return { value: 1 };
    };
    await s.load(
      "test-overlap",
      `on("ui.render", async ($, e) => {
         await Promise.all([$.store.get("a"), $.store.get("b"), $.session.title()]);
         return ${text("done")};
       });`,
    );
    const r = await s.render(["test-overlap@v1"], band);
    expect(r.failures).toEqual([]);
    expect(r.result.children).toEqual(["done"]);
  });

  test("next.budget.remainingMs shrinks as the hook spends its own time", async () => {
    const s = setup({ hookMs: 1000 });
    await s.load(
      "test-remaining",
      `on("turn.complete", async ($, e, next) => {
         const before = next.budget.remainingMs;
         const t = Date.now() + 30; while (Date.now() < t) {}
         $.ui.log(String(before - next.budget.remainingMs >= 25));
         return next(e);
       });`,
    );
    await s.dispatch("turn.complete", ["test-remaining@v1"], { sessionId: "ses_test1", turnId: "t", isAborted: false, isFailed: false });
    expect(logs(s)).toEqual(["true"]);
  });
});

describe(".catch", () => {
  test("a handler answers for a hook that threw; it is not listed and not a strike", async () => {
    const s = setup();
    await s.load(
      "test-catch",
      `on("ui.render", () => { throw new Error("broken"); }).catch(($, e, next) => ${text("recovered")});`,
    );
    const r = await s.render(["test-catch@v1"], band);
    expect(r.result.children).toEqual(["recovered"]);
    expect(r.failures).toEqual([]);
    expect(r.drawnBy).toEqual(["test-catch@v1"]);
  });

  test("the handler sees next.error and next.called", async () => {
    const s = setup();
    await s.load(
      "test-catch-info",
      `on("turn.complete", () => { throw new Error("kaput"); }).catch(($, e, next) => { $.ui.log(next.error.kind + ":" + next.error.message + ":" + next.called); return next(e); });
       on("ui.render", async ($, e, next) => { await next(e); throw new Error("after"); }).catch(($, e, next) => { $.ui.log(next.error.kind + ":" + next.error.message + ":" + next.called); return next(e); });`,
    );
    await s.dispatch("turn.complete", ["test-catch-info@v1"], { sessionId: "ses_test1", turnId: "t", isAborted: false, isFailed: false });
    await s.render(["test-catch-info@v1"], band);
    expect(logs(s)).toEqual(["throw:kaput:false", "throw:after:true"]);
  });

  test("when the hook had called next, the handler's next replays that result without re-running the chain", async () => {
    const s = setup();
    await s.load(
      "test-replay",
      `on("ui.render", async ($, e, next) => { await next(e); throw new Error("x"); }).catch(async ($, e, next) => { const a = await next(e); const b = await next(e); return a; });`,
    );
    await s.load("test-count", `on("ui.render", ($, e, next) => { $.ui.log("inner"); return next(e); });`);
    const r = await s.render(["test-replay@v1", "test-count@v1"], band);
    expect(logs(s)).toEqual(["inner"]);
    expect(r.result).toEqual({ type: "Fleet" });
    expect(r.failures).toEqual([]);
  });

  test("when the hook hadn't called next, the handler's next runs the rest once and later calls replay it", async () => {
    const s = setup();
    await s.load("test-nonext", `on("ui.render", () => { throw new Error("x"); }).catch(async ($, e, next) => { await next(e); return next(e); });`);
    await s.load("test-count", `on("ui.render", ($, e, next) => { $.ui.log("inner"); return next(e); });`);
    await s.render(["test-nonext@v1", "test-count@v1"], band);
    expect(logs(s)).toEqual(["inner"]);
  });

  test("the handler has its own catchMs budget", async () => {
    const s = setup({ hookMs: 500, catchMs: 40 });
    await s.load(
      "test-catch-slow",
      `on("ui.render", () => { throw new Error("x"); }).catch(async ($, e) => { await new Promise((r) => $.clock.after(120, r)); return ${text("too late")}; });`,
    );
    const r = await s.render(["test-catch-slow@v1"], band);
    expect(r.result).toEqual({ type: "Fleet" });
    expect(r.failures).toMatchObject([{ kind: "throw", message: "x", strikes: 1 }]);
  });

  test("a handler that fails too leaves the failure standing as a strike", async () => {
    const s = setup();
    await s.load("test-catch-fails", `on("ui.render", () => { throw new Error("first"); }).catch(() => { throw new Error("second"); });`);
    const r = await s.render(["test-catch-fails@v1"], band);
    expect(r.failures).toMatchObject([{ message: "first", strikes: 1 }]);
  });

  test("a handler's answer is checked like a hook's: an invalid tree is no answer", async () => {
    const s = setup();
    await s.load("test-catch-bad", `on("ui.render", () => { throw new Error("first"); }).catch(($, e) => $.ui.resolve(e).Box({ children: [] }));`);
    const r = await s.render(["test-catch-bad@v1"], chip);
    expect(r.failures).toHaveLength(1);
  });

  test("an answered failure neither counts nor resets strikes", async () => {
    const s = setup();
    await s.load("test-mix", `on("turn.complete", () => { throw new Error("x"); });
      on("ui.render", () => { throw new Error("y"); }).catch(($, e) => ${text("ok")});`);
    const e = { sessionId: "ses_test1", turnId: "t", isAborted: false, isFailed: false };
    await s.dispatch("turn.complete", ["test-mix@v1"], e);
    await s.render(["test-mix@v1"], band);
    const r = await s.dispatch("turn.complete", ["test-mix@v1"], e);
    expect(r.failures[0].strikes).toBe(2);
  });
});

describe("failure without a handler", () => {
  test("a hook that failed before next is skipped and the rest runs in its place with the e it got", async () => {
    const s = setup();
    await s.load("test-fails", `on("ui.render", () => { throw new Error("nope"); });`);
    await s.load("test-after", `on("ui.render", ($, e) => ${"$.ui.resolve(e).Text({ children: [e.requestId] })"});`);
    const r = await s.render(["test-fails@v1", "test-after@v1"], band);
    expect(r.result.children).toEqual(["ses_test1"]);
    expect(r.failures).toMatchObject([{ mod: "test-fails@v1", event: "ui.render", kind: "throw", message: "nope", strikes: 1 }]);
  });

  test("a hook that failed after next resolved leaves that result standing", async () => {
    const s = setup();
    await s.load("test-late-fail", `on("ui.render", async ($, e, next) => { await next(e); throw new Error("after"); });`);
    await s.load("test-inner", `on("ui.render", ($, e) => ${text("inner")});`);
    const r = await s.render(["test-late-fail@v1", "test-inner@v1"], band);
    expect(r.result.children).toEqual(["inner"]);
    expect(r.failures).toHaveLength(1);
  });

  test("a failure with a next call still in flight waits for it and uses it", async () => {
    const s = setup();
    await s.load("test-inflight", `on("ui.render", ($, e, next) => { next(e); throw new Error("fire and forget"); });`);
    await s.load("test-inner", `on("ui.render", async ($, e) => { await new Promise((r) => $.clock.after(20, r)); $.ui.log("inner ran"); return ${text("inner")}; });`);
    const r = await s.render(["test-inflight@v1", "test-inner@v1"], band);
    expect(r.result.children).toEqual(["inner"]);
    expect(logs(s)).toEqual(["inner ran"]);
  });

  test("an unknown element is a throw failure with the reason", async () => {
    const s = setup();
    await s.load("test-bad-tree", `on("ui.render", () => ({ type: "Marquee", props: {} }));`);
    const r = await s.render(["test-bad-tree@v1"], band);
    expect(r.failures[0].kind).toBe("throw");
    expect(r.failures[0].message).toContain("Marquee");
    expect(r.result).toEqual({ type: "Fleet" });
  });

  test("a Box column at an inline-only site is a throw failure", async () => {
    const s = setup();
    await s.load("test-inline", `on("ui.render", ($, e) => $.ui.resolve(e).Box({ flexDirection: "column", children: [] }));`);
    const r = await s.render(["test-inline@v1"], chip);
    expect(r.failures[0].kind).toBe("throw");
    expect(r.failures[0].message).toContain("row");
  });

  test("undefined from ui.render is a failure: returned nothing", async () => {
    const s = setup();
    await s.load("test-undef", `on("ui.render", () => {});`);
    const r = await s.render(["test-undef@v1"], band);
    expect(r.failures[0].message).toContain("returned nothing");
  });

  test("a press or input that returns the wrong shape is a failure", async () => {
    const s = setup();
    await s.load("test-shape", `on("ui.render", ($, e) => $.ui.resolve(e).Button({ key: "go", label: "Go", onPress: () => {} }));`);
    const drawn = await s.render(["test-shape@v1"], band);
    const handle = drawn.result.handles.onPress;
    await s.load("test-swallow", `on("ui.press", () => ({ nope: 1 }));`);
    const r = await s.dispatch("ui.press", ["test-swallow@v1"], { sessionId: "ses_test1", mod: "test-shape@v1", element: "go", component: "ComposerBand", requestId: "ses_test1", surface: "desktop", handle });
    expect(r.failures).toHaveLength(1);
  });
});

describe("strikes", () => {
  test("three failures in a row report strikes 1, 2, 3 and unload the mod", async () => {
    const s = setup();
    await s.load("test-three", `on("ui.render", () => { throw new Error("again"); });`);
    const seen: number[] = [];
    for (let i = 0; i < 3; i++) seen.push((await s.render(["test-three@v1"], band)).failures[0].strikes);
    expect(seen).toEqual([1, 2, 3]);
    const after = await s.render(["test-three@v1"], band);
    expect(after.failures).toEqual([]);
    expect(after.result).toEqual({ type: "Fleet" });
  });

  test("the strike that unloads a mod skips its remaining hooks in the same dispatch", async () => {
    const s = setup({ strikes: 1 });
    await s.load("test-skip", `on("turn.complete", () => { throw new Error("1"); });\non("turn.complete", {isFailed: false}, ($, e, next) => { $.ui.log("second hook"); return next(e); });`);
    const r = await s.dispatch("turn.complete", ["test-skip@v1"], { sessionId: "ses_test1", turnId: "t", isAborted: false, isFailed: false });
    expect(r.failures).toHaveLength(1);
    expect(logs(s)).toEqual([]);
  });

  test("a success resets the count", async () => {
    const s = setup();
    await s.load("test-flaky", `on("ui.render", ($, e, next) => { if (e.requestId === "bad") throw new Error("x"); return next(e); });`);
    const bad = renderE("ComposerBand", "bad", { isWorking: false });
    await s.render(["test-flaky@v1"], bad);
    await s.render(["test-flaky@v1"], bad);
    await s.render(["test-flaky@v1"], band);
    expect((await s.render(["test-flaky@v1"], bad)).failures[0].strikes).toBe(1);
  });

  test("strikes are per mod id", async () => {
    const s = setup();
    await s.load("test-x", `on("ui.render", () => { throw new Error("x"); });`);
    await s.load("test-y", `on("ui.render", () => { throw new Error("y"); });`);
    const r = await s.render(["test-x@v1", "test-y@v1"], band);
    expect(r.failures.map((f: any) => [f.mod, f.strikes])).toEqual([["test-x@v1", 1], ["test-y@v1", 1]]);
  });
});

describe("aborted calls", () => {
  const tc = { sessionId: "ses_test1", turnId: "t", isAborted: false, isFailed: false };
  const WAITS = `on("turn.complete", async ($, e, next) => { await new Promise((r) => $.clock.after(100, r)); return next(e); });`;

  test("an unload mid-hook settles the call at once as skipped, with no failure", async () => {
    const s = setup({ hookMs: 2000 });
    await s.load("test-u", WAITS);
    const t0 = Date.now();
    const p = s.dispatch("turn.complete", ["test-u@v1"], tc);
    await sleep(20);
    await s.peer.call("unload", { id: "test-u@v1" });
    const r = await p;
    expect(Date.now() - t0).toBeLessThan(200);
    expect(r.failures).toEqual([]);
  });

  test("a forget mid-hook settles the call at once as skipped, with no failure", async () => {
    const s = setup({ hookMs: 2000 });
    await s.load("test-f", WAITS);
    const t0 = Date.now();
    const p = s.dispatch("turn.complete", ["test-f@v1"], tc);
    await sleep(20);
    await s.peer.call("forget", { sessionId: "ses_test1" });
    const r = await p;
    expect(Date.now() - t0).toBeLessThan(200);
    expect(r.failures).toEqual([]);
  });

  test("a reload mid-hook settles the old call at once, and the new module isn't struck for it", async () => {
    const s = setup({ hookMs: 300 });
    await s.load("test-r", WAITS);
    const t0 = Date.now();
    const p = s.dispatch("turn.complete", ["test-r@v1"], tc);
    await sleep(20);
    await s.load("test-r", `on("turn.complete", ($, e, next) => next(e));`);
    const r = await p;
    expect(Date.now() - t0).toBeLessThan(200);
    expect(r.failures).toEqual([]);
  });

  test("a fresh load after an unload mid-hook starts at strike 0", async () => {
    const s = setup({ hookMs: 100 });
    await s.load("test-u", WAITS);
    const p = s.dispatch("turn.complete", ["test-u@v1"], tc);
    await sleep(20);
    await s.peer.call("unload", { id: "test-u@v1" });
    await p;
    await s.load("test-u", `on("turn.complete", () => { throw new Error("new"); });`);
    const r = await s.dispatch("turn.complete", ["test-u@v1"], tc);
    expect(r.failures.map((f: any) => f.strikes)).toEqual([1]);
  });

  test("a timer of a replaced module that fails late doesn't strike the module that replaced it", async () => {
    const s = setup();
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    s.peer.answers["store.get"] = async () => {
      await gate;
      return {};
    };
    await s.load("test-old", `on("turn.complete", ($, e, next) => { $.clock.after(0, async () => { await $.store.get("k"); throw new Error("old timer"); }); return next(e); });`);
    await s.dispatch("turn.complete", ["test-old@v1"], tc);
    await sleep(10);
    await s.load("test-old", `on("turn.complete", () => { throw new Error("new"); });`);
    release();
    await sleep(10);
    expect(s.peer.notes("failed")).toEqual([]);
    const r = await s.dispatch("turn.complete", ["test-old@v1"], tc);
    expect(r.failures.map((f: any) => f.strikes)).toEqual([1]);
  });
});
