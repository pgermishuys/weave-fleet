import { describe, expect, test } from "bun:test";
import { renderE, setup, sleep, until } from "../helpers/harness";

const band = renderE("ComposerBand", "ses_test1", { isWorking: false });
const logs = (s: ReturnType<typeof setup>) => s.peer.notes("log").map((l) => l.text);
const START = `on("session.start", async ($, e, next) => { $.ui.log("start " + e.sessionId + " " + e.reason); return next(e); });
on("ui.render", ($, e) => $.ui.resolve(e).Text({ children: ["x"] }));`;

describe("session.start", () => {
  test("another event first runs the mod's session.start with reason start, once per session", async () => {
    const s = setup();
    await s.load("test-start", START);
    await s.render(["test-start@v1"], band);
    await s.render(["test-start@v1"], band);
    expect(logs(s)).toEqual(["start ses_test1 start"]);
    await s.render(["test-start@v1"], { ...band, sessionId: "ses_test2" }, "ses_test2");
    expect(logs(s)).toEqual(["start ses_test1 start", "start ses_test2 start"]);
  });

  test("a session.start dispatch runs the hooks with Fleet's e, once, and later events don't repeat it", async () => {
    const s = setup();
    await s.load("test-start", START);
    await s.dispatch("session.start", ["test-start@v1"], { sessionId: "ses_test1", reason: "reload" });
    await s.dispatch("session.start", ["test-start@v1"], { sessionId: "ses_test1", reason: "start" });
    await s.render(["test-start@v1"], band);
    expect(logs(s)).toEqual(["start ses_test1 reload"]);
  });

  test("a session.start dispatch only runs mods that haven't started the session", async () => {
    const s = setup();
    await s.load("test-a", START);
    await s.load("test-b", START);
    await s.render(["test-a@v1"], band);
    s.peer.notifications.length = 0;
    await s.dispatch("session.start", ["test-a@v1", "test-b@v1"], { sessionId: "ses_test1", reason: "start" });
    expect(s.peer.notes("log").map((l) => l.mod)).toEqual(["test-b@v1"]);
  });

  test("the start runs first, before the mod's own hook for the event", async () => {
    const s = setup();
    await s.load("test-order", `${START}\non("turn.complete", ($, e, next) => { $.ui.log("turn"); return next(e); });`);
    await s.dispatch("turn.complete", ["test-order@v1"], { sessionId: "ses_test1", turnId: "t", isAborted: false, isFailed: false });
    expect(logs(s)).toEqual(["start ses_test1 start", "turn"]);
  });

  test("concurrent dispatches share one run of the start", async () => {
    const s = setup();
    await s.load("test-shared", `on("session.start", async ($, e, next) => { $.ui.log("start"); await new Promise((r) => $.clock.after(40, r)); return next(e); });
      on("ui.render", ($, e) => $.ui.resolve(e).Text({ children: ["x"] }));`);
    const [a, b] = await Promise.all([s.render(["test-shared@v1"], band), s.render(["test-shared@v1"], band)]);
    expect(logs(s)).toEqual(["start"]);
    expect(a.result.type).toBe("Text");
    expect(b.result.type).toBe("Text");
  });

  test("a failure in a start run is listed in the dispatch that ran it", async () => {
    const s = setup();
    await s.load("test-startfail", `on("session.start", () => { throw new Error("start broke"); });\non("ui.render", ($, e) => $.ui.resolve(e).Text({ children: ["x"] }));`);
    const r = await s.render(["test-startfail@v1"], band);
    expect(r.failures).toMatchObject([{ mod: "test-startfail@v1", event: "session.start", kind: "throw", message: "start broke", strikes: 1 }]);
    expect(r.result.type).toBe("Text");
  });

  test("draft hooks only start for their own session", async () => {
    const s = setup();
    await s.load("test-d", START, { version: "draft", sessionId: "ses_test1" });
    await s.render(["test-d@draft:ses_test1"], { ...band, sessionId: "ses_test2" }, "ses_test2");
    expect(logs(s)).toEqual([]);
  });

  test("forget lets the mod start the session again", async () => {
    const s = setup();
    await s.load("test-again", START);
    await s.render(["test-again@v1"], band);
    await s.peer.call("forget", { sessionId: "ses_test1" });
    await s.render(["test-again@v1"], band);
    expect(logs(s)).toEqual(["start ses_test1 start", "start ses_test1 start"]);
  });

  test("a failed reload-start is a failed notification, after the load answers", async () => {
    const s = setup();
    await s.load("test-rl", START);
    await s.render(["test-rl@v1"], band);
    s.peer.notifications.length = 0;
    await s.load("test-rl", `on("session.start", () => { throw new Error("reload start broke"); });`);
    await until(() => s.peer.notes("failed").length > 0, "failed");
    expect(s.peer.notes("failed")[0]).toEqual({ mod: "test-rl@v1", event: "session.start", kind: "throw", message: "reload start broke", strikes: 1, sessionId: "ses_test1" });
  });

  test("a reload only restarts sessions the old module had started", async () => {
    const s = setup();
    await s.load("test-rl2", START);
    await s.render(["test-rl2@v1"], band);
    s.peer.notifications.length = 0;
    await s.load("test-rl2", START);
    await sleep(30);
    expect(logs(s)).toEqual(["start ses_test1 reload"]);
  });

  test("a dispatch arriving before the reload start runs it with reason reload", async () => {
    const s = setup();
    await s.load("test-rl3", START);
    await s.render(["test-rl3@v1"], band);
    s.peer.notifications.length = 0;
    await s.load("test-rl3", START);
    await s.render(["test-rl3@v1"], band);
    await sleep(30);
    expect(logs(s)).toEqual(["start ses_test1 reload"]);
  });

  test("two quick reloads both start every session with reason reload, even ones the middle module hadn't reached", async () => {
    const s = setup({ hookMs: 300 });
    const body = `on("session.start", async ($, e, next) => { $.ui.log(e.sessionId + ":" + e.reason); await new Promise((r) => $.clock.after(50, r)); return next(e); });`;
    await s.load("test-dr", body);
    await s.dispatch("session.start", ["test-dr@v1"], { sessionId: "ses_1", reason: "start" }, "ses_1");
    await s.dispatch("session.start", ["test-dr@v1"], { sessionId: "ses_2", reason: "start" }, "ses_2");
    await s.load("test-dr", body);
    await sleep(10);
    s.peer.notifications.length = 0;
    await s.load("test-dr", body);
    await s.host.idle();
    await s.dispatch("turn.complete", ["test-dr@v1"], { sessionId: "ses_2", turnId: "t", isAborted: false, isFailed: false }, "ses_2");
    expect(logs(s).sort()).toEqual(["ses_1:reload", "ses_2:reload"]);
    expect(s.peer.notes("failed")).toEqual([]);
  });
});
