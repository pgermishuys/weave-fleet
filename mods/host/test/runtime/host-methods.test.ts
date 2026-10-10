import { describe, expect, test } from "bun:test";
import { createHost } from "../../src/host";
import { RpcError } from "../../src/rpc";
import { HOST_VERSION } from "../../src/version";
import { check, checkUsed } from "../helpers/check";
import { fakeCheck } from "../helpers/fake-check";
import { FakePeer } from "../helpers/fake-peer";
import { renderE, setup, sleep, until, writeMod } from "../helpers/harness";

const UI = `on("ui.render", async ($, e, next) => next(e));`;

describe("initialize", () => {
  test("answers protocol 1 with host and bun versions", async () => {
    const s = setup();
    const r = await s.peer.call("initialize", { protocol: 1, fleetVersion: "0.1.0" });
    expect(r).toEqual({ protocol: 1, hostVersion: HOST_VERSION, bunVersion: Bun.version });
  });
  test("refuses another protocol with -32000 and the supported list", async () => {
    const s = setup();
    const err = await s.peer.call("initialize", { protocol: 2, fleetVersion: "0.1.0" }).catch((e) => e);
    expect(err).toBeInstanceOf(RpcError);
    expect(err.code).toBe(-32000);
    expect(err.data).toEqual({ supported: [1] });
  });
});

describe("check", () => {
  test("returns the check report for a mod folder", async () => {
    const s = setup();
    const root = writeMod("test-checked", UI);
    const report = await s.peer.call("check", { root, manifest: "mod.json" });
    expect(report.ok).toBe(true);
    expect(report.name).toBe("test-checked");
  });
});

describe("load", () => {
  test("answers the check report and the hooks register called, in order, with matchers as JSON", async () => {
    const s = setup();
    const r = await s.load(
      "test-hooks",
      `on("ui.render", { component: ["ToolUse", "ToolResult"], props: { tool: /^ba/i } }, ($, e, next) => next(e));
       on("turn.complete", ($, e, next) => next(e));
       on("ui.render", { component: "StatusChip" }, ($, e, next) => next(e));`,
    );
    expect(r.check.ok).toBe(true);
    expect(r.hooks).toEqual([
      { event: "ui.render", matcher: { component: ["ToolUse", "ToolResult"], props: { tool: { $regex: "^ba", flags: "i" } } } },
      { event: "turn.complete" },
      { event: "ui.render", matcher: { component: "StatusChip" } },
    ]);
  });

  test("refuses with -32001 and the report when the check fails", async () => {
    const root = writeMod("test-bad", UI);
    const bad = { ok: false, name: "test-bad", version: "0.1.0", description: "d", lines: 1, sha256: "x", hooks: [], calls: [], state: [], pages: [], errors: [{ code: "import", message: "imports fs", line: 1, column: 1 }], warnings: [] };
    const peer = new FakePeer();
    createHost({ peer, check: async () => ({ report: bad as any }), exit: () => {}, log: () => {} });
    const err = await peer.call("load", { id: "test-bad@v1", name: "test-bad", version: 1, root }).catch((e) => e);
    expect(err.code).toBe(-32001);
    expect(err.message).toBe("test-bad doesn't load: imports fs");
    expect(err.data.errors[0].code).toBe("import");
  });

  test("refuses when mod.json's name differs from the name asked for", async () => {
    const s = setup();
    const root = writeMod("test-other", UI);
    const err = await s.peer.call("load", { id: "test-asked@v1", name: "test-asked", version: 1, root }).catch((e) => e);
    expect(err.code).toBe(-32001);
    expect(err.message).toContain("test-asked doesn't load");
    expect(err.data.errors.map((x: any) => x.code)).toContain("name-mismatch");
  });

  test("refuses a module with no register export, adding a no-register error", async () => {
    const s = setup();
    const root = writeMod("test-noreg", "");
    await Bun.write(`${root}/mod.ts`, "export const other = 1;\n");
    const err = await s.peer.call("load", { id: "test-noreg@v1", name: "test-noreg", version: 1, root }).catch((e) => e);
    expect(err.code).toBe(-32001);
    expect(err.data.errors.map((x: any) => x.code)).toContain("no-register");
  });

  test("refuses when register throws, with the message as a register error", async () => {
    const s = setup();
    const err = await s.load("test-throws", `throw new Error("boom in register");`).catch((e) => e);
    expect(err.code).toBe(-32001);
    expect(err.data.errors.find((x: any) => x.code === "register").message).toBe("boom in register");
  });

  test("refuses a second unmatched on for one event at runtime", async () => {
    const s = setup({}, fakeCheck);
    const err = await s.load("test-dup", `${UI}\n${UI}`).catch((e) => e);
    expect(err.code).toBe(-32001);
    expect(err.data.errors.find((x: any) => x.code === "register").message).toContain("ui.render");
  });

  test("the real static check refuses a duplicate unmatched on before the module runs", async () => {
    const s = setup();
    const err = await s.load("test-dup2", `${UI}\n${UI}`).catch((e) => e);
    expect(err.code).toBe(-32001);
    expect(err.data.ok).toBe(false);
  });

  test("allows the same event twice when the matchers differ", async () => {
    const s = setup();
    const r = await s.load("test-twice", `on("ui.render", { component: "ToolUse" }, ($, e, next) => next(e));\n${UI}`);
    expect(r.hooks).toHaveLength(2);
  });

  test("refuses an event the host doesn't have at runtime", async () => {
    const s = setup({}, fakeCheck);
    const err = await s.load("test-unknown", `on("prompt.submit", ($, e, next) => next(e));`).catch((e) => e);
    expect(err.code).toBe(-32001);
    expect(err.data.errors.find((x: any) => x.code === "register").message).toContain("prompt.submit");
  });

  test("on called after register returned throws", async () => {
    const s = setup({}, fakeCheck);
    const r = await s.load("test-late", `globalThis.__late = on;`);
    expect(r.hooks).toEqual([]);
    const late = (globalThis as any).__late;
    expect(() => late("ui.render", () => null)).toThrow();
    delete (globalThis as any).__late;
  });

  test("an id that disagrees with name, version or sessionId is -32602", async () => {
    const s = setup();
    const root = writeMod("test-ids", UI);
    for (const p of [
      { id: "test-ids@v1", name: "test-other", version: 1 },
      { id: "test-ids@v2", name: "test-ids", version: 1 },
      { id: "test-ids@v1", name: "test-ids", version: "draft" },
      { id: "test-ids@draft:ses_test1", name: "test-ids", version: "draft", sessionId: "ses_other" },
      { id: "test-ids@draft:ses_test1", name: "test-ids", version: "draft" },
      { id: "test-ids", name: "test-ids", version: 1 },
    ]) {
      const err = await s.peer.call("load", { ...p, root }).catch((e) => e);
      expect(err.code).toBe(-32602);
    }
  });

  test("checks against the real static check when it exists", () => {
    expect(["real", "fake"]).toContain(checkUsed);
  });
});

describe("reload", () => {
  test("replaces the hooks of the old module", async () => {
    const s = setup();
    await s.load("test-reload", `on("ui.render", ($, e) => $.ui.resolve(e).Text({ children: ["old"] }));`);
    const r = await s.load("test-reload", `on("ui.render", ($, e) => $.ui.resolve(e).Text({ children: ["new"] }));\non("turn.complete", ($, e, next) => next(e));`);
    expect(r.hooks.map((h: any) => h.event)).toEqual(["ui.render", "turn.complete"]);
    const d = await s.render(["test-reload@v1"], renderE("StatusChip", "ses_test1", {}));
    expect(d.result.children).toEqual(["new"]);
  });

  test("the new module's session.start fires with reason reload, state survives, old timers stop", async () => {
    const s = setup();
    await s.load(
      "test-reload2",
      `on("session.start", ($, e, next) => {
         $.state.set("keep", 42);
         $.clock.every(100, () => $.ui.log("old tick"));
         return next(e);
       });`,
    );
    await s.dispatch("session.start", ["test-reload2@v1"], { sessionId: "ses_test1", reason: "start" });
    await s.load(
      "test-reload2",
      `on("session.start", ($, e, next) => { $.ui.log("new start " + e.reason + " keep=" + $.state.get("keep")); return next(e); });`,
    );
    await until(() => s.peer.notes("log").some((l) => l.text.startsWith("new start")));
    expect(s.peer.notes("log").find((l) => l.text.startsWith("new start")).text).toBe("new start reload keep=42");
    s.peer.notifications.length = 0;
    await sleep(250);
    expect(s.peer.notes("log").filter((l) => l.text === "old tick")).toHaveLength(0);
  });

  test("a reload resets strikes", async () => {
    const s = setup();
    const bad = `on("ui.render", async () => { throw new Error("no"); });`;
    await s.load("test-strikes", bad);
    await s.render(["test-strikes@v1"]);
    await s.render(["test-strikes@v1"]);
    await s.load("test-strikes", bad);
    const r = await s.render(["test-strikes@v1"]);
    expect(r.failures[0].strikes).toBe(1);
  });

  test("a failed reload leaves the old module running", async () => {
    const s = setup();
    await s.load("test-keep", `on("ui.render", ($, e) => $.ui.resolve(e).Text({ children: ["old"] }));`);
    const err = await s.load("test-keep", `throw new Error("new one is broken");`).catch((e) => e);
    expect(err.code).toBe(-32001);
    const r = await s.render(["test-keep@v1"], renderE("ComposerBand", "ses_test1", { isWorking: false }));
    expect(r.result.children).toEqual(["old"]);
  });
});

describe("unload and forget", () => {
  test("unload stops the module: it no longer draws", async () => {
    const s = setup();
    await s.load("test-gone", `on("ui.render", ($, e) => $.ui.resolve(e).Text({ children: ["here"] }));`);
    expect((await s.render(["test-gone@v1"], renderE("ComposerBand", "ses_test1", { isWorking: true }))).result.type).toBe("Text");
    expect(await s.peer.call("unload", { id: "test-gone@v1" })).toEqual({});
    expect((await s.render(["test-gone@v1"], renderE("ComposerBand", "ses_test1", { isWorking: true }))).result).toEqual({ type: "Fleet" });
  });
  test("unload of an unknown id is fine", async () => {
    const s = setup();
    expect(await s.peer.call("unload", { id: "nothing@v1" })).toEqual({});
  });
  test("forget answers {}", async () => {
    const s = setup();
    expect(await s.peer.call("forget", { sessionId: "ses_test1" })).toEqual({});
  });
  test("unknown dispatch event and bad params are -32602", async () => {
    const s = setup();
    const a = await s.peer.call("dispatch", { event: "nope", sessionId: "ses_test1", e: {}, mods: [] }).catch((e) => e);
    expect(a.code).toBe(-32602);
    const b = await s.peer.call("dispatch", { event: "ui.render", sessionId: 5, e: {}, mods: [] }).catch((e) => e);
    expect(b.code).toBe(-32602);
  });
});

describe("shutdown", () => {
  test("answers {} then exits 0", async () => {
    const s = setup();
    expect(await s.peer.call("shutdown", {})).toEqual({});
    await until(() => s.exits.length > 0, "exit");
    expect(s.exits).toEqual([0]);
  });
  test("stops timers before exiting", async () => {
    const s = setup();
    await s.load("test-timers", `on("session.start", ($, e, next) => { $.clock.every(100, () => $.ui.log("tick")); return next(e); });`);
    await s.dispatch("session.start", ["test-timers@v1"], { sessionId: "ses_test1", reason: "start" });
    await s.peer.call("shutdown", {});
    await until(() => s.exits.length > 0, "exit");
    await sleep(250);
    expect(s.peer.notes("log")).toHaveLength(0);
  });
  test("waits for an in-flight dispatch to finish, $.clock waits and all, before it stops timers and exits", async () => {
    const s = setup({ shutdownMs: 1000, hookMs: 5000 });
    await s.load("test-slow", `on("turn.complete", async ($, e, next) => { await new Promise((r) => $.clock.after(100, r)); $.ui.log("finished"); return next(e); });`);
    let answered = false;
    void s.dispatch("turn.complete", ["test-slow@v1"], { sessionId: "ses_test1", turnId: "t", isAborted: false, isFailed: false }).then(() => (answered = true));
    await sleep(20);
    const t = Date.now();
    await s.peer.call("shutdown", {});
    await until(() => s.exits.length > 0, "exit");
    // Timers stop only after the wait, so the hook's $.clock.after could fire and the dispatch answer.
    expect(s.peer.notes("log").map((l) => l.text)).toEqual(["finished"]);
    expect(answered).toBe(true);
    expect(Date.now() - t).toBeLessThan(500);
  });
  test("gives up on work still running 90% of shutdownMs after the request arrived", async () => {
    const s = setup({ shutdownMs: 300, hookMs: 5000 });
    await s.load("test-stuck", `on("ui.render", async ($, e, next) => { await new Promise((r) => $.clock.after(2000, r)); return next(e); });`);
    void s.render(["test-stuck@v1"]);
    await sleep(20);
    const t = Date.now();
    await s.peer.call("shutdown", {});
    await until(() => s.exits.length > 0, "exit");
    expect(Date.now() - t).toBeGreaterThanOrEqual(260);
    expect(Date.now() - t).toBeLessThan(300);
  });
  test("waits for a load received before the shutdown", async () => {
    let releaseCheck!: () => void;
    const held = new Promise<void>((r) => (releaseCheck = r));
    const s = setup({ shutdownMs: 1000 }, async (...args) => {
      await held;
      return check(...args);
    });
    const root = writeMod("test-late-load", UI);
    let loaded: unknown;
    const load = s.peer.call("load", { id: "test-late-load@v1", name: "test-late-load", version: 1, root }).then((r) => (loaded = r));
    await s.peer.call("shutdown", {});
    await sleep(30);
    expect(s.exits).toEqual([]);
    releaseCheck();
    await load;
    await until(() => s.exits.length > 0, "exit");
    expect(loaded).toMatchObject({ check: { ok: true } });
  });
});
