import { describe, expect, test } from "bun:test";
import { renderE, setup, sleep, until } from "../helpers/harness";

const band = renderE("ComposerBand", "ses_test1", { isWorking: false });
const turn = { sessionId: "ses_test1", turnId: "t1", isAborted: false, isFailed: false };
const logs = (s: ReturnType<typeof setup>) => s.peer.notes("log").map((l) => l.text);
const text = `$.ui.resolve(e).Text({ children: ["x"] })`;
/** Loads a mod whose turn.complete hook runs `body` and logs what it returns or throws. */
async function runTurn(s: ReturnType<typeof setup>, name: string, body: string, extra = "") {
  await s.load(name, `${extra}\non("turn.complete", async ($, e, next) => { try { const r = await (async () => { ${body} })(); $.ui.log("ok " + JSON.stringify(r)); } catch (err) { $.ui.log("threw " + err.name + ": " + err.message); } return next(e); });`);
  s.peer.notifications.length = 0;
  await s.dispatch("turn.complete", [`${name}@v1`], turn);
}

describe("$.state", () => {
  test("get returns what set stored, as a deep-frozen copy", async () => {
    const s = setup();
    await runTurn(s, "test-state", `$.state.set("k", { a: [1, 2] }); const v = $.state.get("k"); let t = "no"; try { v.a.push(3); } catch { t = "frozen"; } return [v, t, $.state.get("missing")];`);
    expect(logs(s)).toEqual([`ok [{"a":[1,2]},"frozen",null]`]);
  });
  test("state is per mod and per session", async () => {
    const s = setup();
    await s.load("test-s1", `on("turn.complete", ($, e, next) => { $.ui.log("before " + $.state.get("k")); $.state.set("k", e.sessionId); return next(e); });`);
    await s.load("test-s2", `on("turn.complete", ($, e, next) => { $.ui.log("s2 " + $.state.get("k")); return next(e); });`);
    await s.dispatch("turn.complete", ["test-s1@v1", "test-s2@v1"], turn);
    await s.dispatch("turn.complete", ["test-s1@v1", "test-s2@v1"], { ...turn, sessionId: "ses_test2" }, "ses_test2");
    await s.dispatch("turn.complete", ["test-s1@v1"], turn);
    expect(logs(s)).toEqual(["before undefined", "s2 undefined", "before undefined", "s2 undefined", "before ses_test1"]);
  });
  test("values must be JSON", async () => {
    const s = setup();
    await runTurn(s, "test-json", `const out = []; for (const v of [undefined, () => 1, NaN, Infinity, new Date(), new Map(), Symbol("x"), 10n]) { try { $.state.set("k", v); out.push("ok"); } catch { out.push("threw"); } } return out;`);
    expect(logs(s)).toEqual([`ok ["threw","threw","threw","threw","threw","threw","threw","threw"]`]);
  });
  test("a circular value throws", async () => {
    const s = setup();
    await runTurn(s, "test-circ", `const a = {}; a.self = a; $.state.set("k", a);`);
    expect(logs(s)[0]).toStartWith("threw");
  });
  test("a mod's state can't pass stateBytes of JSON", async () => {
    const s = setup({ stateBytes: 100 });
    await runTurn(s, "test-big", `$.state.set("a", "x".repeat(60)); try { $.state.set("b", "y".repeat(60)); } catch (err) { return err.name; } return "no throw";`);
    expect(logs(s)).toEqual([`ok "RangeError"`]);
  });
  test("overwriting a key counts its new size, not both", async () => {
    const s = setup({ stateBytes: 100 });
    await runTurn(s, "test-over", `for (let i = 0; i < 5; i++) $.state.set("a", "x".repeat(60)); return "fine";`);
    expect(logs(s)).toEqual([`ok "fine"`]);
  });
  test("a ui.render hook can't write $.state", async () => {
    const s = setup();
    await s.load("test-ro", `on("ui.render", ($, e) => { try { $.state.set("k", 1); } catch (err) { $.ui.log(err.message); } return ${text}; });`);
    await s.render(["test-ro@v1"], band);
    expect(logs(s)).toEqual(["a ui.render hook can't write $.state"]);
  });
  test("the same $ captured by a callback can write after the render hook settled", async () => {
    const s = setup();
    await s.load("test-cb", `on("ui.render", ($, e) => { $.clock.after(10, () => { $.state.set("k", 1); $.ui.log("wrote " + $.state.get("k")); }); return ${text}; });`);
    await s.render(["test-cb@v1"], band);
    await until(() => logs(s).length > 0);
    expect(logs(s)).toEqual(["wrote 1"]);
  });
  test("reading a key in render subscribes: a later write sends invalidate", async () => {
    const s = setup();
    await s.load(
      "test-sub",
      `on("ui.render", ($, e) => { $.state.get("count"); return ${text}; });
       on("turn.complete", ($, e, next) => { $.state.set("count", 1); $.state.set("other", 1); return next(e); });`,
    );
    await s.render(["test-sub@v1"], band);
    expect(s.peer.notes("invalidate")).toEqual([]);
    await s.dispatch("turn.complete", ["test-sub@v1"], turn);
    expect(s.peer.notes("invalidate")).toEqual([{ mod: "test-sub@v1", sessionId: "ses_test1" }]);
  });
  test("a write to a key nobody read in render sends nothing", async () => {
    const s = setup();
    await s.load("test-nosub", `on("turn.complete", ($, e, next) => { $.state.set("other", 1); return next(e); });`);
    await s.dispatch("turn.complete", ["test-nosub@v1"], turn);
    expect(s.peer.notes("invalidate")).toEqual([]);
  });
  test("a write from a timer invalidates the subscribed mod in that session", async () => {
    const s = setup();
    await s.load("test-timer-write", `on("ui.render", ($, e) => { const v = $.state.get("n"); $.clock.after(5, () => $.state.set("n", 1)); return ${text}; });`);
    await s.render(["test-timer-write@v1"], band);
    await until(() => s.peer.notes("invalidate").length > 0, "invalidate");
    expect(s.peer.notes("invalidate")[0]).toEqual({ mod: "test-timer-write@v1", sessionId: "ses_test1" });
  });
  test("forget drops the session's state", async () => {
    const s = setup();
    await s.load("test-forget", `on("turn.complete", ($, e, next) => { $.ui.log("n=" + $.state.get("n")); $.state.set("n", 1); return next(e); });`);
    await s.dispatch("turn.complete", ["test-forget@v1"], turn);
    await s.peer.call("forget", { sessionId: "ses_test1" });
    await s.dispatch("turn.complete", ["test-forget@v1"], turn);
    expect(logs(s)).toEqual(["n=undefined", "n=undefined"]);
  });
  test("unload drops the mod's state", async () => {
    const s = setup();
    const body = `on("turn.complete", ($, e, next) => { $.ui.log("n=" + $.state.get("n")); $.state.set("n", 1); return next(e); });`;
    await s.load("test-unl", body);
    await s.dispatch("turn.complete", ["test-unl@v1"], turn);
    await s.peer.call("unload", { id: "test-unl@v1" });
    await s.load("test-unl", body);
    await s.dispatch("turn.complete", ["test-unl@v1"], turn);
    expect(logs(s)).toEqual(["n=undefined", "n=undefined"]);
  });
});

describe("$.ui.invalidate", () => {
  test("only 'ui.render' is accepted", async () => {
    const s = setup();
    await runTurn(s, "test-inv-arg", `$.ui.invalidate("turn.complete");`);
    expect(logs(s)[0]).toStartWith("threw");
  });
  test("the first goes at once; a burst of ten coalesces into one more at the window's end", async () => {
    const s = setup({ invalidatePerSecond: 20 });
    await s.load("test-burst", `on("turn.complete", ($, e, next) => { for (let i = 0; i < 10; i++) $.ui.invalidate("ui.render"); return next(e); });`);
    await s.dispatch("turn.complete", ["test-burst@v1"], turn);
    expect(s.peer.notes("invalidate")).toHaveLength(1);
    await sleep(120);
    expect(s.peer.notes("invalidate")).toHaveLength(2);
    await sleep(120);
    expect(s.peer.notes("invalidate")).toHaveLength(2);
  });
  test("the throttle is per mod and per session", async () => {
    const s = setup({ invalidatePerSecond: 20 });
    const body = `on("turn.complete", ($, e, next) => { $.ui.invalidate("ui.render"); $.ui.invalidate("ui.render"); return next(e); });`;
    await s.load("test-t1", body);
    await s.load("test-t2", body);
    await s.dispatch("turn.complete", ["test-t1@v1", "test-t2@v1"], turn);
    await s.dispatch("turn.complete", ["test-t1@v1"], { ...turn, sessionId: "ses_test2" }, "ses_test2");
    expect(s.peer.notes("invalidate")).toHaveLength(3);
  });
  test("forget and unload drop a pending invalidation", async () => {
    const s = setup({ invalidatePerSecond: 10 });
    await s.load("test-drop", `on("turn.complete", ($, e, next) => { $.ui.invalidate("ui.render"); $.ui.invalidate("ui.render"); return next(e); });`);
    await s.dispatch("turn.complete", ["test-drop@v1"], turn);
    await s.peer.call("forget", { sessionId: "ses_test1" });
    await sleep(160);
    expect(s.peer.notes("invalidate")).toHaveLength(1);
  });
});

describe("$.clock", () => {
  test("now is Date.now", async () => {
    const s = setup();
    await runTurn(s, "test-now", `return Math.abs($.clock.now() - Date.now()) < 50;`);
    expect(logs(s)).toEqual(["ok true"]);
  });
  test("after fires once; every repeats; cancel stops them", async () => {
    const s = setup({ timerMinMs: 20 });
    await s.load(
      "test-timers",
      `on("turn.complete", ($, e, next) => {
         $.clock.after(10, () => $.ui.log("after"));
         let n = 0;
         const t = $.clock.every(20, () => { n++; $.ui.log("every " + n); if (n === 3) t.cancel(); });
         const c = $.clock.after(10, () => $.ui.log("cancelled ran"));
         c.cancel();
         return next(e);
       });`,
    );
    await s.dispatch("turn.complete", ["test-timers@v1"], turn);
    await sleep(200);
    expect(logs(s)).toEqual(["after", "every 1", "every 2", "every 3"]);
  });
  test("every below timerMinMs throws RangeError", async () => {
    const s = setup();
    await runTurn(s, "test-min", `$.clock.every(99, () => {});`);
    expect(logs(s)).toEqual(["threw RangeError: $.clock.every needs at least 100 ms"]);
  });
  test("more than timersPerSession live timers per mod per session throws", async () => {
    const s = setup({ timersPerSession: 3 });
    await runTurn(s, "test-max", `const out = []; for (let i = 0; i < 5; i++) { try { $.clock.after(5000, () => {}); out.push(1); } catch { out.push(0); } } return out;`);
    expect(logs(s)).toEqual(["ok [1,1,1,0,0]"]);
  });
  test("a fired or cancelled timer frees its slot", async () => {
    const s = setup({ timersPerSession: 1 });
    await runTurn(s, "test-free", `const a = $.clock.after(5000, () => {}); a.cancel(); $.clock.after(5000, () => {}); return "ok";`);
    expect(logs(s)).toEqual([`ok "ok"`]);
  });
  test("a timer that throws sends failed and counts a strike", async () => {
    const s = setup();
    await s.load("test-tfail", `on("turn.complete", ($, e, next) => { $.clock.after(5, () => { throw new Error("tick broke"); }); return next(e); });`);
    await s.dispatch("turn.complete", ["test-tfail@v1"], turn);
    await until(() => s.peer.notes("failed").length > 0, "failed");
    expect(s.peer.notes("failed")[0]).toEqual({ mod: "test-tfail@v1", event: "turn.complete", kind: "throw", message: "tick broke", strikes: 1, sessionId: "ses_test1" });
  });
  test("a timer that rejects is a failure too, and three strikes unload the mod", async () => {
    const s = setup({ timerMinMs: 10 });
    await s.load("test-trej", `on("turn.complete", ($, e, next) => { $.clock.every(10, async () => { throw new Error("async broke"); }); return next(e); });`);
    await s.dispatch("turn.complete", ["test-trej@v1"], turn);
    await until(() => s.peer.notes("failed").length >= 3, "three failures");
    await sleep(60);
    expect(s.peer.notes("failed").map((f) => f.strikes)).toEqual([1, 2, 3]);
    const r = await s.dispatch("turn.complete", ["test-trej@v1"], turn);
    expect(r.failures).toEqual([]);
  });
  test("an every whose previous run hasn't settled skips that tick", async () => {
    const s = setup({ timerMinMs: 10 });
    await s.load("test-skip", `on("turn.complete", ($, e, next) => { $.clock.every(10, async () => { $.ui.log("run"); await new Promise((r) => $.clock.after(70, r)); }); return next(e); });`);
    await s.dispatch("turn.complete", ["test-skip@v1"], turn);
    await sleep(55);
    expect(logs(s)).toEqual(["run"]);
  });
  test("forget and unload stop a session's / a mod's timers", async () => {
    const s = setup({ timerMinMs: 10 });
    await s.load("test-stop", `on("turn.complete", ($, e, next) => { $.clock.every(10, () => $.ui.log("tick " + e.sessionId)); return next(e); });`);
    await s.dispatch("turn.complete", ["test-stop@v1"], turn);
    await s.dispatch("turn.complete", ["test-stop@v1"], { ...turn, sessionId: "ses_test2" }, "ses_test2");
    await sleep(40);
    await s.peer.call("forget", { sessionId: "ses_test1" });
    s.peer.notifications.length = 0;
    await sleep(50);
    expect(new Set(logs(s))).toEqual(new Set(["tick ses_test2"]));
    await s.peer.call("unload", { id: "test-stop@v1" });
    s.peer.notifications.length = 0;
    await sleep(50);
    expect(logs(s)).toEqual([]);
  });
});

describe("$.store and $.session", () => {
  test("store.get/set/delete/keys send requests carrying mod and sessionId", async () => {
    const s = setup();
    await runTurn(s, "test-store", `await $.store.set("a", { n: 1 }); const got = await $.store.get("a"); const keys = await $.store.keys(); await $.store.delete("a"); return [got, keys, await $.store.get("a")];`);
    expect(logs(s)).toEqual([`ok [{"n":1},["a"],null]`]);
    expect(s.peer.reqs("store.set")).toEqual([{ mod: "test-store@v1", sessionId: "ses_test1", key: "a", value: { n: 1 } }]);
    expect(s.peer.reqs("store.get")[0]).toEqual({ mod: "test-store@v1", sessionId: "ses_test1", key: "a" });
    expect(s.peer.reqs("store.keys")).toEqual([{ mod: "test-store@v1", sessionId: "ses_test1" }]);
    expect(s.peer.reqs("store.delete")).toHaveLength(1);
  });
  test("store values must be JSON", async () => {
    const s = setup();
    await runTurn(s, "test-store-json", `await $.store.set("a", () => 1);`);
    expect(logs(s)[0]).toStartWith("threw");
    expect(s.peer.reqs("store.set")).toEqual([]);
  });
  test("session.id is local; title, harness, cwd and surfaces ask session.get", async () => {
    const s = setup();
    await runTurn(s, "test-session", `return [await $.session.id(), await $.session.title(), await $.session.harness(), await $.session.cwd(), await $.session.surfaces()];`);
    expect(logs(s)).toEqual([`ok ["ses_test1","Invented title","opencode","/work/demo",["desktop"]]`]);
    expect(s.peer.reqs("session.get")).toHaveLength(4);
    expect(s.peer.reqs("session.get")[0]).toEqual({ mod: "test-session@v1", sessionId: "ses_test1" });
  });
  test("a request Fleet answers with an error rejects in the mod", async () => {
    const s = setup();
    s.peer.answers["store.get"] = () => {
      throw new Error("store is down");
    };
    await runTurn(s, "test-store-err", `await $.store.get("a");`);
    expect(logs(s)).toEqual(["threw Error: store is down"]);
  });
});

describe("$.ui", () => {
  test("open and close send requests, and check the id", async () => {
    const s = setup();
    await runTurn(s, "test-pane", `await $.ui.open({ id: "main-1", title: "Main" }); await $.ui.close({ id: "main-1" }); const out = []; for (const id of ["", "has space", "x".repeat(65), "a/b"]) { try { await $.ui.open({ id }); out.push("ok"); } catch { out.push("threw"); } } return out;`);
    expect(logs(s)).toEqual([`ok ["threw","threw","threw","threw"]`]);
    expect(s.peer.reqs("ui.open")).toEqual([{ mod: "test-pane@v1", sessionId: "ses_test1", id: "main-1", title: "Main" }]);
    expect(s.peer.reqs("ui.close")).toEqual([{ mod: "test-pane@v1", sessionId: "ses_test1", id: "main-1" }]);
  });
  test("toast cuts the text to 500 characters and doesn't wait", async () => {
    const s = setup();
    s.peer.answers["ui.toast"] = () => new Promise(() => {});
    await runTurn(s, "test-toast", `$.ui.toast("y".repeat(900), { timeoutMs: 1000, tone: "warn" }); return "returned";`);
    expect(logs(s)).toEqual([`ok "returned"`]);
    const t = s.peer.reqs("ui.toast")[0];
    expect(t.text).toHaveLength(500);
    expect(t).toMatchObject({ mod: "test-toast@v1", sessionId: "ses_test1", timeoutMs: 1000, tone: "warn" });
  });
  test("a toast Fleet refuses is logged to stderr, not thrown", async () => {
    const s = setup();
    s.peer.answers["ui.toast"] = () => {
      throw new Error("nope");
    };
    await runTurn(s, "test-toast2", `$.ui.toast("hi");`);
    await until(() => s.logs.some((l) => l.includes("toast")), "stderr log");
    expect(s.peer.notes("failed")).toEqual([]);
  });
  test("log sends a log notification with level (default info), mod and session", async () => {
    const s = setup();
    await s.load("test-log", `on("turn.complete", ($, e, next) => { $.ui.log("one"); $.ui.log("two", { level: "warn" }); return next(e); });`);
    await s.dispatch("turn.complete", ["test-log@v1"], turn);
    expect(s.peer.notes("log")).toEqual([
      { mod: "test-log@v1", sessionId: "ses_test1", level: "info", text: "one" },
      { mod: "test-log@v1", sessionId: "ses_test1", level: "warn", text: "two" },
    ]);
  });
  test("a log line is cut at 8 KiB", async () => {
    const s = setup();
    await runTurn(s, "test-biglog", `$.ui.log("z".repeat(20000));`);
    expect(s.peer.notes("log")[0].text).toHaveLength(8192);
  });
  test("console.* inside a mod becomes a log notification with the right level", async () => {
    const s = setup();
    await s.load(
      "test-console",
      `on("turn.complete", ($, e, next) => {
         console.log("a", 1, { b: 2 }); console.info("i"); console.debug("d"); console.warn("w"); console.error("e");
         return next(e);
       });`,
    );
    await s.dispatch("turn.complete", ["test-console@v1"], turn);
    expect(s.peer.notes("log").map((l) => [l.level, l.text])).toEqual([
      ["info", `a 1 ${Bun.inspect({ b: 2 })}`],
      ["info", "i"],
      ["info", "d"],
      ["warn", "w"],
      ["error", "e"],
    ]);
    expect(s.peer.notes("log")[0].sessionId).toBe("ses_test1");
  });
  test("console at module top level and in register logs without a session", async () => {
    const s = setup();
    await s.load("test-console-load", `console.log("in register");`);
    expect(s.peer.notes("log")).toEqual([{ mod: "test-console-load@v1", level: "info", text: "in register" }]);
  });
  test("$.ui.resolve returns factories tagged with this mod", async () => {
    const s = setup();
    await s.load("test-resolve", `on("ui.render", ($, e) => $.ui.resolve(e).Pill({ tone: "good", label: "ok" }));`);
    const r = await s.render(["test-resolve@v1"], renderE("StatusChip", "ses_test1", {}));
    expect(r.result).toEqual({ type: "Pill", props: { tone: "good", label: "ok" } });
    expect(r.drawnBy).toEqual(["test-resolve@v1"]);
  });
});
