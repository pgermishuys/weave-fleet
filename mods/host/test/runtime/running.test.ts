import { describe, expect, test } from "bun:test";
import { renderE, setup } from "../helpers/harness";

const band = renderE("ComposerBand", "ses_test1", { isWorking: false });
const turn = { sessionId: "ses_test1", turnId: "t1", isAborted: false, isFailed: false };
const running = (s: ReturnType<typeof setup>) => s.peer.notes("running");
const orderOf = (s: ReturnType<typeof setup>) =>
  s.peer.notifications.filter((n) => n.method === "running" || n.method === "log").map((n) => (n.method === "log" ? `log ${n.params.text}` : `running ${n.params.mod} ${n.params.event}`));
const BUTTON = `on("ui.render", ($, e) => $.ui.resolve(e).Button({ key: "go", label: "Go", onPress: () => $.ui.log("pressed") }));`;

describe("the running notification", () => {
  test("a two-mod chain announces each hook, in order, before it runs", async () => {
    const s = setup();
    await s.load("test-a", `on("turn.complete", ($, e, next) => { $.ui.log("a"); return next(e); });`);
    await s.load("test-b", `on("turn.complete", ($, e, next) => { $.ui.log("b"); return next(e); });`);
    await s.dispatch("turn.complete", ["test-a@v1", "test-b@v1"], turn);
    expect(running(s)).toEqual([
      { mod: "test-a@v1", event: "turn.complete", sessionId: "ses_test1" },
      { mod: "test-b@v1", event: "turn.complete", sessionId: "ses_test1" },
    ]);
    expect(orderOf(s)).toEqual(["running test-a@v1 turn.complete", "log a", "running test-b@v1 turn.complete", "log b"]);
  });

  test("a one-mod chain announces nothing", async () => {
    const s = setup();
    await s.load("test-a", `on("turn.complete", ($, e, next) => next(e));\n${BUTTON}`);
    await s.dispatch("turn.complete", ["test-a@v1"], turn);
    await s.render(["test-a@v1"], band);
    expect(running(s)).toEqual([]);
  });

  test("ids that are not loaded don't count toward the chain", async () => {
    const s = setup();
    await s.load("test-a", `on("turn.complete", ($, e, next) => next(e));`);
    await s.dispatch("turn.complete", ["ghost@v1", "test-a@v1"], turn);
    expect(running(s)).toEqual([]);
  });

  test("only hooks that run are announced", async () => {
    const s = setup();
    await s.load("test-a", `on("ui.render", { component: "StatusChip" }, ($, e, next) => next(e));`);
    await s.load("test-b", `on("ui.render", ($, e, next) => next(e));`);
    await s.render(["test-a@v1", "test-b@v1"], band);
    expect(running(s).map((r) => r.mod)).toEqual(["test-b@v1"]);
  });

  test("a .catch handler is announced after its hook, in a multi-mod chain", async () => {
    const s = setup();
    await s.load("test-a", `on("ui.render", ($, e, next) => { throw new Error("boom"); }).catch(($, e, next) => next(e));`);
    await s.load("test-b", `on("ui.render", ($, e, next) => next(e));`);
    await s.render(["test-a@v1", "test-b@v1"], band);
    expect(running(s).map((r) => r.mod)).toEqual(["test-a@v1", "test-a@v1", "test-b@v1"]);
  });

  test("the control callback at the end of a press is announced with its owner", async () => {
    const s = setup();
    await s.load("test-btn", BUTTON);
    await s.load("test-spy", `on("ui.press", ($, e, next) => next(e));`);
    const h = (await s.render(["test-btn@v1"], band)).result.handles.onPress;
    s.peer.notifications.length = 0;
    const e = { sessionId: "ses_test1", mod: "test-btn@v1", element: "go", component: "ComposerBand", requestId: "ses_test1", surface: "desktop", handle: h };
    await s.dispatch("ui.press", ["test-spy@v1", "test-btn@v1"], e);
    expect(orderOf(s)).toEqual(["running test-spy@v1 ui.press", "running test-btn@v1 ui.press", "log pressed"]);
  });

  test("the implicit session.start run before an event is announced when the chain has several mods", async () => {
    const s = setup();
    await s.load("test-a", `on("session.start", ($, e, next) => next(e));\non("turn.complete", ($, e, next) => next(e));`);
    await s.load("test-b", `on("turn.complete", ($, e, next) => next(e));`);
    await s.dispatch("turn.complete", ["test-a@v1", "test-b@v1"], turn);
    expect(running(s).map((r) => `${r.mod} ${r.event}`)).toEqual(["test-a@v1 session.start", "test-a@v1 turn.complete", "test-b@v1 turn.complete"]);
  });

  test("a session.start dispatch with several mods is announced too", async () => {
    const s = setup();
    await s.load("test-a", `on("session.start", ($, e, next) => next(e));`);
    await s.load("test-b", `on("session.start", ($, e, next) => next(e));`);
    await s.dispatch("session.start", ["test-a@v1", "test-b@v1"], { sessionId: "ses_test1", reason: "start" });
    expect(running(s).map((r) => r.mod)).toEqual(["test-a@v1", "test-b@v1"]);
  });
});
