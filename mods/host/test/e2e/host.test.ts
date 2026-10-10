import { afterEach, describe, expect, test } from "bun:test";
import { join } from "node:path";
import { HostClient } from "../helpers/rpc-client";
import { sleep, until, writeMod } from "../helpers/harness";

const CHIPS = join(import.meta.dir, "..", "fixtures", "mods", "test-chips");
const clients: HostClient[] = [];
const start = () => {
  const c = new HostClient();
  clients.push(c);
  return c;
};
afterEach(() => {
  for (const c of clients.splice(0)) c.kill();
});

const DOTNET = {
  tool: "bash",
  rawTool: "Bash",
  category: "shell",
  status: "completed",
  title: "dotnet test",
  input: { command: "dotnet test" },
  inputTruncated: false,
  output: "Failed!  - Failed: 2, Passed: 212, Skipped: 4, Total: 218\n  Failed Fleet.Tests.Alpha\n  Failed Fleet.Tests.Beta\n",
  outputTruncated: false,
};

async function loadChips(c: HostClient) {
  await c.request("initialize", { protocol: 1, fleetVersion: "0.0.0-test" });
  return c.request("load", { id: "test-chips@v1", name: "test-chips", version: 1, root: CHIPS });
}
const render = (c: HostClient, component: string, props: unknown) =>
  c.request("dispatch", { event: "ui.render", sessionId: "ses_test1", e: { component, sessionId: "ses_test1", requestId: "call_1", props }, mods: ["test-chips@v1"] });

describe("the real host over stdio", () => {
  test("initialize answers the protocol and versions", async () => {
    const c = start();
    const r = await c.request("initialize", { protocol: 1, fleetVersion: "0.0.0-test" });
    expect(r).toMatchObject({ protocol: 1, hostVersion: expect.any(String), bunVersion: Bun.version });
    const err = await c.request("initialize", { protocol: 7, fleetVersion: "x" }).catch((e) => e);
    expect(err.code).toBe(-32000);
    expect(err.data).toEqual({ supported: [1] });
  });

  test("check and load test-chips report its hooks", async () => {
    const c = start();
    await c.request("initialize", { protocol: 1, fleetVersion: "x" });
    const report = await c.request("check", { root: CHIPS, manifest: "mod.json" });
    expect(report.ok).toBe(true);
    expect(report.name).toBe("test-chips");
    const loaded = await c.request("load", { id: "test-chips@v1", name: "test-chips", version: 1, root: CHIPS });
    expect(loaded.check.ok).toBe(true);
    expect(loaded.hooks).toEqual([{ event: "ui.render", matcher: { component: ["ToolUse", "ToolResult"], props: { tool: ["bash", "shell"] } } }]);
  });

  test("a dotnet test row draws three pills", async () => {
    const c = start();
    await loadChips(c);
    const r = await render(c, "ToolUse", DOTNET);
    expect(r.drawnBy).toEqual(["test-chips@v1"]);
    expect(r.failures).toEqual([]);
    expect(r.result).toEqual({
      type: "Box",
      props: { flexDirection: "row", gap: 1 },
      children: [
        { type: "Pill", props: { tone: "good", label: "212 passed" } },
        { type: "Pill", props: { tone: "bad", label: "2 failed" } },
        { type: "Pill", props: { tone: "neutral", label: "4 skipped" } },
      ],
    });
  });

  test("an opened ToolResult lists the failing names, then Fleet's own body", async () => {
    const c = start();
    await loadChips(c);
    const r = await render(c, "ToolResult", DOTNET);
    expect(r.result).toEqual({
      type: "Box",
      props: { flexDirection: "column", gap: 2 },
      children: [
        { type: "Text", props: { code: true, color: "bad" }, children: ["Fleet.Tests.Alpha"] },
        { type: "Text", props: { code: true, color: "bad" }, children: ["Fleet.Tests.Beta"] },
        { type: "Fleet" },
      ],
    });
  });

  test("a row that isn't a test run draws as Fleet does", async () => {
    const c = start();
    await loadChips(c);
    const r = await render(c, "ToolUse", { ...DOTNET, input: { command: "ls -la" }, output: "total 0" });
    expect(r.result).toEqual({ type: "Fleet" });
    expect(r.drawnBy).toBeUndefined();
  });

  test("a mod's Button is pressed end to end", async () => {
    const c = start();
    await c.request("initialize", { protocol: 1, fleetVersion: "x" });
    const root = writeMod(
      "demo-band",
      `on("ui.render", { component: "ComposerBand" }, ($, e) => $.ui.resolve(e).Button({ key: "go", label: "Go", onPress: async () => { await $.store.set("pressed", true); $.ui.log("pressed"); } }));`,
    );
    await c.request("load", { id: "demo-band@v1", name: "demo-band", version: 1, root });
    const drawn = await c.request("dispatch", { event: "ui.render", sessionId: "ses_test1", e: { component: "ComposerBand", sessionId: "ses_test1", requestId: "ses_test1", props: { isWorking: false } }, mods: ["demo-band@v1"] });
    expect(drawn.result.type).toBe("Button");
    const handle = drawn.result.handles.onPress;
    const r = await c.request("dispatch", { event: "ui.press", sessionId: "ses_test1", e: { sessionId: "ses_test1", mod: "demo-band@v1", element: "go", component: "ComposerBand", requestId: "ses_test1", surface: "desktop", handle }, mods: [] });
    expect(r.result).toEqual({ element: "go" });
    expect(c.requestsFromHost.find((x) => x.method === "store.set")?.params).toEqual({ mod: "demo-band@v1", sessionId: "ses_test1", key: "pressed", value: true });
    expect(c.notes("log").map((l) => l.text)).toEqual(["pressed"]);
  });

  test("store and session requests reach the fake Fleet and come back", async () => {
    const c = start();
    await c.request("initialize", { protocol: 1, fleetVersion: "x" });
    const root = writeMod("demo-asks", `on("ui.render", async ($, e) => $.ui.resolve(e).Text({ children: [await $.store.get("k"), " in ", await $.session.title()] }));`);
    await c.request("load", { id: "demo-asks@v1", name: "demo-asks", version: 1, root });
    const r = await c.request("dispatch", { event: "ui.render", sessionId: "ses_test1", e: { component: "ComposerBand", sessionId: "ses_test1", requestId: "ses_test1", props: { isWorking: false } }, mods: ["demo-asks@v1"] });
    expect(r.result.children).toEqual(["stored", " in ", "Invented title"]);
  });

  test("a garbage line is -32700 and the host keeps going", async () => {
    const c = start();
    c.sendRaw("this is not json");
    await until(() => c.lines.some((l: any) => l?.error?.code === -32700), "parse error");
    const r = await c.request("initialize", { protocol: 1, fleetVersion: "x" });
    expect(r.protocol).toBe(1);
  });

  test("stdout carries only JSON lines: a mod's console.log becomes a log notification", async () => {
    const c = start();
    await c.request("initialize", { protocol: 1, fleetVersion: "x" });
    const root = writeMod("demo-loud", `console.log("loud at load");\non("turn.complete", ($, e, next) => { console.log("loud in hook", { n: 1 }); return next(e); });`);
    await c.request("load", { id: "demo-loud@v1", name: "demo-loud", version: 1, root });
    await c.request("dispatch", { event: "turn.complete", sessionId: "ses_test1", e: { sessionId: "ses_test1", turnId: "t", isAborted: false, isFailed: false }, mods: ["demo-loud@v1"] });
    expect(c.notes("log").map((l) => l.text)).toEqual(["loud at load", `loud in hook ${Bun.inspect({ n: 1 })}`]);
    for (const line of c.rawStdout.split("\n").filter(Boolean)) expect(() => JSON.parse(line)).not.toThrow();
    expect(c.rawStdout).not.toContain("loud at load\n{");
  });

  test("a load that fails answers -32001 with the report", async () => {
    const c = start();
    await c.request("initialize", { protocol: 1, fleetVersion: "x" });
    const root = writeMod("demo-bad", `on("ui.render", ($, e) => { process.exit(1); });`);
    const err = await c.request("load", { id: "demo-bad@v1", name: "demo-bad", version: 1, root }).catch((e) => e);
    expect(err.code).toBe(-32001);
    expect(err.data.ok).toBe(false);
    expect(err.data.errors.length).toBeGreaterThan(0);
  });

  test("shutdown answers {} and the host exits 0 within 2 seconds", async () => {
    const c = start();
    await c.request("initialize", { protocol: 1, fleetVersion: "x" });
    const t = Date.now();
    expect(await c.request("shutdown", {})).toEqual({});
    expect(await c.exited).toBe(0);
    expect(Date.now() - t).toBeLessThan(2000);
  });

  test("the host exits when stdin closes", async () => {
    const c = start();
    await c.request("initialize", { protocol: 1, fleetVersion: "x" });
    c.closeStdin();
    expect(await Promise.race([c.exited, sleep(2000).then(() => "still running")])).toBe(0);
  });

  test("without --stdio it prints usage to stderr and exits 2", async () => {
    const c = new HostClient(["src/main.ts"]);
    clients.push(c);
    expect(await c.exited).toBe(2);
    await sleep(20);
    expect(c.stderr).toContain("usage");
    expect(c.rawStdout).toBe("");
  });
});
