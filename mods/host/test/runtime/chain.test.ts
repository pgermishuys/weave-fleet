import { describe, expect, test } from "bun:test";
import { renderE, setup, sleep, until } from "../helpers/harness";

const text = (s: string) => `$.ui.resolve(e).Text({ children: [${JSON.stringify(s)}] })`;
const band = renderE("ComposerBand", "ses_test1", { isWorking: false });
const turn = { sessionId: "ses_test1", turnId: "t1", isAborted: false, isFailed: false };

describe("chain order and matching", () => {
  test("the chain runs mods outermost first, and hooks within a module in on order", async () => {
    const s = setup();
    await s.load("test-a", `on("turn.complete", ($, e, next) => { $.ui.log("a1"); return next(e); });\non("turn.complete", {isFailed: false}, ($, e, next) => { $.ui.log("a2"); return next(e); });`);
    await s.load("test-b", `on("turn.complete", ($, e, next) => { $.ui.log("b1"); return next(e); });`);
    await s.dispatch("turn.complete", ["test-a@v1", "test-b@v1"], turn);
    expect(s.peer.notes("log").map((l) => l.text)).toEqual(["a1", "a2", "b1"]);
    s.peer.notifications.length = 0;
    await s.dispatch("turn.complete", ["test-b@v1", "test-a@v1"], turn);
    expect(s.peer.notes("log").map((l) => l.text)).toEqual(["b1", "a1", "a2"]);
  });

  test("ids that are not loaded are skipped", async () => {
    const s = setup();
    await s.load("test-a", `on("ui.render", ($, e) => ${text("a")});`);
    const r = await s.render(["ghost@v1", "test-a@v1"], band);
    expect(r.result.children).toEqual(["a"]);
  });

  test("a hook whose matcher doesn't match is skipped", async () => {
    const s = setup();
    await s.load("test-a", `on("ui.render", { component: "StatusChip" }, ($, e) => ${text("chip")});`);
    expect((await s.render(["test-a@v1"], band)).result).toEqual({ type: "Fleet" });
    expect((await s.render(["test-a@v1"], renderE("StatusChip", "ses_test1", {}))).result.children).toEqual(["chip"]);
  });

  test("the matcher sees the event as dispatched, not as an earlier hook rewrote it", async () => {
    const s = setup();
    await s.load("test-a", `on("ui.render", ($, e, next) => next({ ...e, props: { isWorking: true } }));`);
    await s.load("test-b", `on("ui.render", { props: { isWorking: false } }, ($, e) => ${text("b ran")});`);
    const r = await s.render(["test-a@v1", "test-b@v1"], band);
    expect(r.result.children).toEqual(["b ran"]);
  });
});

describe("what a hook sees and does", () => {
  test("e is deeply frozen", async () => {
    const s = setup();
    await s.load(
      "test-frozen",
      `on("ui.render", ($, e, next) => {
         let top = "no", deep = "no";
         try { e.requestId = "x"; } catch { top = "threw"; }
         try { e.props.input.command = "x"; } catch { deep = "threw"; }
         $.ui.log(top + " " + deep + " " + Object.isFrozen(e.props.input));
         return next(e);
       });`,
    );
    await s.render(["test-frozen@v1"]);
    expect(s.peer.notes("log")[0].text).toBe("threw threw true");
  });

  test("a hook can rewrite with next({...e}) and the next hook sees the copy", async () => {
    const s = setup();
    await s.load("test-a", `on("ui.render", ($, e, next) => next({ ...e, props: { ...e.props, title: "rewritten" } }));`);
    await s.load("test-b", `on("ui.render", ($, e) => ${"$.ui.resolve(e).Text({ children: [e.props.title] })"});`);
    const r = await s.render(["test-a@v1", "test-b@v1"]);
    expect(r.result.children).toEqual(["rewritten"]);
  });

  test("what a hook passes to next is frozen for the next hook", async () => {
    const s = setup();
    await s.load("test-a", `on("turn.complete", ($, e, next) => { const c = { ...e, model: "m" }; return next(c); });`);
    await s.load("test-b", `on("turn.complete", ($, e, next) => { $.ui.log("b: " + e.model + " " + Object.isFrozen(e)); return next(e); });`);
    await s.dispatch("turn.complete", ["test-a@v1", "test-b@v1"], turn);
    const logs = s.peer.notes("log").map((l) => l.text);
    expect(logs).toContain("b: m true");
  });

  test("a hook that answers without next ends the chain: later hooks don't run", async () => {
    const s = setup();
    await s.load("test-a", `on("ui.render", ($, e) => ${text("answer")});`);
    await s.load("test-b", `on("ui.render", ($, e, next) => { $.ui.log("b ran"); return next(e); });`);
    const r = await s.render(["test-a@v1", "test-b@v1"], band);
    expect(r.result.children).toEqual(["answer"]);
    expect(r.drawnBy).toEqual(["test-a@v1"]);
    expect(s.peer.notes("log")).toHaveLength(0);
  });

  test("calling next twice runs the rest of the chain twice", async () => {
    const s = setup();
    await s.load("test-a", `on("ui.render", async ($, e, next) => { await next(e); return next(e); });`);
    await s.load("test-b", `on("ui.render", ($, e, next) => { $.ui.log("b"); return next(e); });`);
    await s.render(["test-a@v1", "test-b@v1"], band);
    expect(s.peer.notes("log")).toHaveLength(2);
  });

  test("next resolves to what the rest draws, so a hook can wrap it in a Box", async () => {
    const s = setup();
    await s.load(
      "test-wrap",
      `on("ui.render", async ($, e, next) => $.ui.resolve(e).Box({ children: [await next(e)] }));`,
    );
    await s.load("test-inner", `on("ui.render", ($, e) => ${text("inner")});`);
    const r = await s.render(["test-wrap@v1", "test-inner@v1"], band);
    expect(r.result.type).toBe("Box");
    expect(r.result.children[0].children).toEqual(["inner"]);
    expect(r.drawnBy.sort()).toEqual(["test-inner@v1", "test-wrap@v1"]);
  });

  test("next.event, next.budget and next.signal are there", async () => {
    const s = setup({ hookMs: 4000 });
    await s.load(
      "test-props",
      `on("turn.complete", ($, e, next) => {
         $.ui.log(next.event + " " + next.budget.ms + " " + (next.budget.remainingMs <= 4000) + " " + next.signal.aborted);
         return next(e);
       });`,
    );
    await s.dispatch("turn.complete", ["test-props@v1"], turn);
    expect(s.peer.notes("log")[0].text).toBe("turn.complete 4000 true false");
  });

  test("next.signal aborts when the hook times out", async () => {
    const s = setup({ hookMs: 60 });
    await s.load(
      "test-abort",
      `on("ui.render", async ($, e, next) => {
         await new Promise((r) => $.clock.after(200, r));
         return next(e);
       });
       on("turn.complete", async ($, e, next) => {
         next.signal.addEventListener("abort", () => $.ui.log("aborted"));
         await new Promise((r) => $.clock.after(200, r));
         return next(e);
       });`,
    );
    const r = await s.dispatch("turn.complete", ["test-abort@v1"], turn);
    expect(r.failures[0].kind).toBe("timeout");
    expect(s.peer.notes("log").map((l) => l.text)).toContain("aborted");
  });

  test("next.signal aborts on forget of its session and on unload of its mod", async () => {
    const s = setup({ hookMs: 2000 });
    await s.load(
      "test-sig",
      `on("turn.complete", async ($, e, next) => {
         next.signal.addEventListener("abort", () => $.ui.log("aborted " + e.sessionId));
         await new Promise((r) => $.clock.after(300, r));
         return next(e);
       });`,
    );
    void s.dispatch("turn.complete", ["test-sig@v1"], turn);
    await sleep(20);
    await s.peer.call("forget", { sessionId: "ses_test1" });
    expect(s.peer.notes("log").map((l) => l.text)).toContain("aborted ses_test1");
    s.peer.notifications.length = 0;
    void s.dispatch("turn.complete", ["test-sig@v1"], { ...turn, sessionId: "ses_test2" }, "ses_test2");
    await sleep(20);
    await s.peer.call("unload", { id: "test-sig@v1" });
    expect(s.peer.notes("log").map((l) => l.text)).toContain("aborted ses_test2");
  });

  test("next after the hook has settled rejects", async () => {
    const s = setup();
    await s.load(
      "test-late",
      `on("turn.complete", ($, e, next) => {
         $.clock.after(0, async () => { try { await next(e); $.ui.log("late ok"); } catch (err) { $.ui.log("late: " + err.message); } });
         return next(e);
       });`,
    );
    await s.dispatch("turn.complete", ["test-late@v1"], turn);
    await until(() => s.peer.notes("log").length > 0, "late log");
    expect(s.peer.notes("log")[0].text).toBe("late: the dispatch is over");
  });
});

describe("end of chain and results", () => {
  test("ui.render with no hooks draws Fleet, with no drawnBy", async () => {
    const s = setup();
    const r = await s.render([]);
    expect(r).toEqual({ result: { type: "Fleet" }, failures: [] });
  });
  test("session.start and turn.complete resolve to the e they were given", async () => {
    const s = setup();
    await s.load("test-a", `on("turn.complete", ($, e, next) => next({ ...e, cost: 1.5 }));`);
    const r = await s.dispatch("turn.complete", ["test-a@v1"], turn);
    expect(r.result).toEqual({ ...turn, cost: 1.5 });
    expect(r.drawnBy).toBeUndefined();
  });
  test("a null from ui.render draws nothing and names the mod that said so as drawnBy", async () => {
    const s = setup();
    await s.load("test-null", `on("ui.render", () => null);`);
    await s.load("test-pass", `on("ui.render", ($, e, next) => next(e));`);
    const r = await s.render(["test-pass@v1", "test-null@v1"], band);
    expect(r.result).toBeNull();
    expect(r.drawnBy).toEqual(["test-null@v1"]);
  });
  test("watch-only events run every hook; the return value is ignored", async () => {
    const s = setup();
    await s.load("test-a", `on("turn.complete", ($, e) => { $.ui.log("a"); return "ignored"; });`);
    await s.load("test-b", `on("turn.complete", ($, e, next) => { $.ui.log("b"); return next(e); });`);
    const r = await s.dispatch("turn.complete", ["test-a@v1", "test-b@v1"], turn);
    expect(s.peer.notes("log").map((l) => l.text)).toEqual(["a", "b"]);
    expect(r.failures).toEqual([]);
    expect(r.result).toEqual(turn);
  });
  test("a watch-only hook that never calls next still lets the rest run with the e it got", async () => {
    const s = setup();
    await s.load("test-a", `on("turn.complete", async () => {});`);
    await s.load("test-b", `on("turn.complete", ($, e, next) => { $.ui.log("b saw " + e.turnId); return next(e); });`);
    await s.dispatch("turn.complete", ["test-a@v1", "test-b@v1"], turn);
    expect(s.peer.notes("log")[0].text).toBe("b saw t1");
  });
});

describe("draft scope", () => {
  test("a draft only runs for its own session, even if a dispatch lists it", async () => {
    const s = setup();
    await s.load("test-draft", `on("ui.render", ($, e) => ${text("draft")});`, { version: "draft", sessionId: "ses_test1" });
    const own = await s.render(["test-draft@draft:ses_test1"], band, "ses_test1");
    expect(own.result.children).toEqual(["draft"]);
    const other = await s.render(["test-draft@draft:ses_test1"], { ...band, sessionId: "ses_test2" }, "ses_test2");
    expect(other.result).toEqual({ type: "Fleet" });
  });
  test("$.mod carries name and version, draft or a number", async () => {
    const s = setup();
    await s.load("test-mod", `on("turn.complete", ($, e, next) => { $.ui.log($.mod.name + " " + $.mod.version); return next(e); });`, { version: 3, id: "test-mod@v3" });
    await s.dispatch("turn.complete", ["test-mod@v3"], turn);
    await s.load("test-dmod", `on("turn.complete", ($, e, next) => { $.ui.log($.mod.name + " " + $.mod.version); return next(e); });`, { version: "draft", sessionId: "ses_test1" });
    await s.dispatch("turn.complete", ["test-dmod@draft:ses_test1"], turn);
    expect(s.peer.notes("log").map((l) => l.text)).toEqual(["test-mod 3", "test-dmod draft"]);
  });
});
