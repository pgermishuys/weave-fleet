import { describe, expect, test } from "bun:test";
import { renderE, setup, sleep } from "../helpers/harness";

const band = renderE("ComposerBand", "ses_test1", { isWorking: false });
const logs = (s: ReturnType<typeof setup>) => s.peer.notes("log").map((l) => l.text);

const BUTTON = `on("ui.render", ($, e) => $.ui.resolve(e).Button({ key: "go", label: "Go", onPress: () => $.ui.log("pressed " + $.mod.name) }));`;
const FORM = `on("ui.render", ($, e) => $.ui.resolve(e).Box({ children: [
  $.ui.resolve(e).Input({ key: "name", onInput: (v) => $.ui.log("input " + v), onSubmit: (v) => $.ui.log("submit " + v) }),
  $.ui.resolve(e).Select({ key: "pick", options: [{ value: "a", label: "A" }, { value: "b", label: "B" }], onSelect: (v) => $.ui.log("select " + v) }),
] }));`;

function control(event: string, mod: string, handle: string, extra: any = {}) {
  return { sessionId: "ses_test1", mod, element: "go", component: "ComposerBand", requestId: "ses_test1", surface: "desktop", handle, ...extra };
}

describe("handles", () => {
  test("a Button gets a handle h<n>; the tree on the wire carries it, not the function", async () => {
    const s = setup();
    await s.load("test-btn", BUTTON);
    const r = await s.render(["test-btn@v1"], band);
    expect(r.result).toEqual({ type: "Button", props: { key: "go", label: "Go" }, handles: { onPress: expect.stringMatching(/^h\d+$/) } });
  });

  test("a ui.press ends in the callback of the mod that drew it, and answers {element}", async () => {
    const s = setup();
    await s.load("test-btn", BUTTON);
    const h = (await s.render(["test-btn@v1"], band)).result.handles.onPress;
    const r = await s.dispatch("ui.press", [], control("ui.press", "test-btn@v1", h));
    expect(r.result).toEqual({ element: "go" });
    expect(logs(s)).toEqual(["pressed test-btn"]);
  });

  test("another mod's ui.press hook runs first, then the drawing mod's callback", async () => {
    const s = setup();
    await s.load("test-btn", BUTTON);
    await s.load("test-spy", `on("ui.press", ($, e, next) => { $.ui.log("spy saw " + e.element + " handle=" + ("handle" in e)); return next(e); });`);
    const h = (await s.render(["test-btn@v1"], band)).result.handles.onPress;
    await s.dispatch("ui.press", ["test-spy@v1"], control("ui.press", "test-btn@v1", h));
    expect(logs(s)).toEqual(["spy saw go handle=false", "pressed test-btn"]);
  });

  test("a hook that swallows the press stops the callback", async () => {
    const s = setup();
    await s.load("test-btn", BUTTON);
    await s.load("test-swallow", `on("ui.press", ($, e) => ({ element: e.element }));`);
    const h = (await s.render(["test-btn@v1"], band)).result.handles.onPress;
    const r = await s.dispatch("ui.press", ["test-swallow@v1"], control("ui.press", "test-btn@v1", h));
    expect(r.result).toEqual({ element: "go" });
    expect(logs(s)).toEqual([]);
  });

  test("ui.input change goes to onInput and submit to onSubmit, with the value", async () => {
    const s = setup();
    await s.load("test-form", FORM);
    const tree = (await s.render(["test-form@v1"], band)).result;
    const input = tree.children[0].handles;
    expect(Object.keys(input).sort()).toEqual(["onInput", "onSubmit"]);
    const a = await s.dispatch("ui.input", [], control("ui.input", "test-form@v1", input.onInput, { element: "name", kind: "change", value: "al" }));
    const b = await s.dispatch("ui.input", [], control("ui.input", "test-form@v1", input.onSubmit, { element: "name", kind: "submit", value: "alice" }));
    expect(a.result).toEqual({ element: "name", value: "al" });
    expect(b.result).toEqual({ element: "name", value: "alice" });
    expect(logs(s)).toEqual(["input al", "submit alice"]);
  });

  test("ui.select value rewritten by a hook reaches the callback", async () => {
    const s = setup();
    await s.load("test-form", FORM);
    await s.load("test-rewrite", `on("ui.select", ($, e, next) => next({ ...e, value: "b" }));`);
    const tree = (await s.render(["test-form@v1"], band)).result;
    const h = tree.children[1].handles.onSelect;
    const r = await s.dispatch("ui.select", ["test-rewrite@v1"], control("ui.select", "test-form@v1", h, { element: "pick", value: "a" }));
    expect(r.result).toEqual({ element: "pick", value: "b" });
    expect(logs(s)).toEqual(["select b"]);
  });

  test("a handle of the wrong kind for the event is -32602 and no hook runs", async () => {
    const s = setup();
    await s.load("test-form", FORM);
    await s.load("test-spy", `on("ui.input", ($, e, next) => { $.ui.log("spy"); return next(e); });`);
    const tree = (await s.render(["test-form@v1"], band)).result;
    const err = await s.dispatch("ui.input", ["test-spy@v1"], control("ui.input", "test-form@v1", tree.children[0].handles.onInput, { element: "name", kind: "submit", value: "x" })).catch((e) => e);
    expect(err.code).toBe(-32602);
    expect(err.message).toBe("unknown or expired handle");
    const err2 = await s.dispatch("ui.press", [], control("ui.press", "test-form@v1", tree.children[1].handles.onSelect)).catch((e) => e);
    expect(err2.code).toBe(-32602);
    expect(logs(s)).toEqual([]);
  });

  test("an unknown handle is -32602", async () => {
    const s = setup();
    const err = await s.dispatch("ui.press", [], control("ui.press", "x@v1", "h999")).catch((e) => e);
    expect(err.code).toBe(-32602);
  });

  test("a handle from another session is -32602", async () => {
    const s = setup();
    await s.load("test-btn", BUTTON);
    const h = (await s.render(["test-btn@v1"], band)).result.handles.onPress;
    const err = await s.dispatch("ui.press", [], control("ui.press", "test-btn@v1", h, { sessionId: "ses_test2" }), "ses_test2").catch((e) => e);
    expect(err.code).toBe(-32602);
  });

  test("drawing the same site again expires the previous handle", async () => {
    const s = setup();
    await s.load("test-btn", BUTTON);
    const first = (await s.render(["test-btn@v1"], band)).result.handles.onPress;
    const second = (await s.render(["test-btn@v1"], band)).result.handles.onPress;
    expect(second).not.toBe(first);
    const err = await s.dispatch("ui.press", [], control("ui.press", "test-btn@v1", first)).catch((e) => e);
    expect(err.code).toBe(-32602);
    expect((await s.dispatch("ui.press", [], control("ui.press", "test-btn@v1", second))).result).toEqual({ element: "go" });
  });

  test("drawing a different request of the same site keeps the other's handles", async () => {
    const s = setup();
    await s.load("test-btn", BUTTON);
    const a = (await s.render(["test-btn@v1"], renderE("ComposerBand", "req_a", { isWorking: false }))).result.handles.onPress;
    await s.render(["test-btn@v1"], renderE("ComposerBand", "req_b", { isWorking: false }));
    expect((await s.dispatch("ui.press", [], control("ui.press", "test-btn@v1", a))).result).toEqual({ element: "go" });
  });

  test("a redraw that draws nothing drops the previous handles too", async () => {
    const s = setup();
    await s.load("test-btn", `on("ui.render", ($, e, next) => e.props.isWorking ? null : $.ui.resolve(e).Button({ key: "go", label: "Go", onPress: () => {} }));`);
    const h = (await s.render(["test-btn@v1"], band)).result.handles.onPress;
    await s.render(["test-btn@v1"], renderE("ComposerBand", "ses_test1", { isWorking: true }));
    const err = await s.dispatch("ui.press", [], control("ui.press", "test-btn@v1", h)).catch((e) => e);
    expect(err.code).toBe(-32602);
  });

  test("forget drops the session's handles; unload and reload drop the mod's", async () => {
    const s = setup();
    await s.load("test-btn", BUTTON);
    const h1 = (await s.render(["test-btn@v1"], band)).result.handles.onPress;
    await s.peer.call("forget", { sessionId: "ses_test1" });
    expect((await s.dispatch("ui.press", [], control("ui.press", "test-btn@v1", h1)).catch((e) => e)).code).toBe(-32602);
    const h2 = (await s.render(["test-btn@v1"], band)).result.handles.onPress;
    await s.load("test-btn", BUTTON);
    expect((await s.dispatch("ui.press", [], control("ui.press", "test-btn@v1", h2)).catch((e) => e)).code).toBe(-32602);
    const h3 = (await s.render(["test-btn@v1"], band)).result.handles.onPress;
    await s.peer.call("unload", { id: "test-btn@v1" });
    expect((await s.dispatch("ui.press", [], control("ui.press", "test-btn@v1", h3)).catch((e) => e)).code).toBe(-32602);
  });

  test("a callback that throws is a failed notification and a strike; the chain still answers", async () => {
    const s = setup();
    await s.load("test-boom", `on("ui.render", ($, e) => $.ui.resolve(e).Button({ key: "go", label: "Go", onPress: () => { throw new Error("press broke"); } }));`);
    const h = (await s.render(["test-boom@v1"], band)).result.handles.onPress;
    const r = await s.dispatch("ui.press", [], control("ui.press", "test-boom@v1", h));
    expect(r.result).toEqual({ element: "go" });
    expect(r.failures).toEqual([]);
    expect(s.peer.notes("failed")).toEqual([{ mod: "test-boom@v1", event: "ui.press", kind: "throw", message: "press broke", strikes: 1, sessionId: "ses_test1" }]);
  });

  test("an async callback that rejects is a failure; one that hangs past hookMs is a timeout", async () => {
    const s = setup({ hookMs: 50 });
    await s.load("test-rej", `on("ui.render", ($, e) => $.ui.resolve(e).Box({ children: [
      $.ui.resolve(e).Button({ key: "rej", label: "R", onPress: async () => { throw new Error("rejected"); } }),
      $.ui.resolve(e).Button({ key: "hang", label: "H", onPress: () => new Promise(() => {}) }),
    ] }));`);
    const tree = (await s.render(["test-rej@v1"], band)).result;
    await s.dispatch("ui.press", [], control("ui.press", "test-rej@v1", tree.children[0].handles.onPress));
    await s.dispatch("ui.press", [], control("ui.press", "test-rej@v1", tree.children[1].handles.onPress, { element: "hang" }));
    expect(s.peer.notes("failed").map((f) => [f.kind, f.message, f.strikes])).toEqual([["throw", "rejected", 1], ["timeout", expect.any(String), 2]]);
  });

  test("a callback runs in the drawing mod's console context", async () => {
    const s = setup();
    await s.load("test-con", `on("ui.render", ($, e) => $.ui.resolve(e).Button({ key: "go", label: "Go", onPress: () => console.log("clicked") }));`);
    const h = (await s.render(["test-con@v1"], band)).result.handles.onPress;
    await s.dispatch("ui.press", [], control("ui.press", "test-con@v1", h));
    await sleep(0);
    expect(s.peer.notes("log")).toEqual([{ mod: "test-con@v1", sessionId: "ses_test1", level: "info", text: "clicked" }]);
  });

  test("a mod that draws inside another mod's tree owns its own callbacks", async () => {
    const s = setup();
    await s.load("test-outer", `on("ui.render", async ($, e, next) => $.ui.resolve(e).Box({ children: [await next(e)] }));`);
    await s.load("test-inner", BUTTON);
    const tree = (await s.render(["test-outer@v1", "test-inner@v1"], band)).result;
    const h = tree.children[0].handles.onPress;
    await s.dispatch("ui.press", [], control("ui.press", "test-inner@v1", h));
    expect(logs(s)).toEqual(["pressed test-inner"]);
  });
});

describe("hand-written elements", () => {
  const HAND_BUTTON = `on("ui.render", ($, e) => ({ type: "Button", props: { key: "go", label: "Go", onPress: () => { throw new Error("second broke"); } } }));`;

  test("a hand-written element is drawn by the mod whose hook returned it, not the outermost mod", async () => {
    const s = setup();
    await s.load("test-first", `on("ui.render", ($, e, next) => next(e));`);
    await s.load("test-second", `on("ui.render", ($, e) => ({ type: "Text", props: {}, children: ["hi"] }));`);
    const r = await s.render(["test-first@v1", "test-second@v1"], band);
    expect(r.drawnBy).toEqual(["test-second@v1"]);
  });

  test("a hand-written Button's callback belongs to the mod that wrote it: its failure strikes that mod", async () => {
    const s = setup();
    await s.load("test-first", `on("ui.render", ($, e, next) => next(e));`);
    await s.load("test-second", HAND_BUTTON);
    const r = await s.render(["test-first@v1", "test-second@v1"], band);
    expect(r.drawnBy).toEqual(["test-second@v1"]);
    await s.dispatch("ui.press", [], control("ui.press", "test-second@v1", r.result.handles.onPress));
    expect(s.peer.notes("failed").map((f) => [f.mod, f.message])).toEqual([["test-second@v1", "second broke"]]);
  });

  test("a hand-written Button inside another mod's Box is owned by its writer, and its handle dies when the writer unloads", async () => {
    const s = setup();
    await s.load("test-outer", `on("ui.render", async ($, e, next) => { const { Box } = $.ui.resolve(e); return Box({ children: [await next(e)] }); });`);
    await s.load("test-second", HAND_BUTTON);
    const r = await s.render(["test-outer@v1", "test-second@v1"], band);
    expect(r.drawnBy).toEqual(["test-outer@v1", "test-second@v1"]);
    await s.peer.call("unload", { id: "test-second@v1" });
    const err = await s.dispatch("ui.press", [], control("ui.press", "test-second@v1", r.result.children[0].handles.onPress)).catch((e) => e);
    expect(err.code).toBe(-32602);
  });
});
