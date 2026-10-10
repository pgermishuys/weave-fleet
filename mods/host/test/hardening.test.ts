// The host's realm is locked down (src/harden.ts) before any mod runs. bunfig.toml preloads the same lockdown into
// every test file, so the in-process tests here and the rest of the suite run hardened; the stdio tests spawn the real
// host (src/main.ts) with a secret in its environment and a sentinel file on disk, and run the review's escapes.
import { afterAll, describe, expect, test } from "bun:test";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createHost } from "../src/host";
import { fakeCheck } from "./helpers/fake-check";
import { FakePeer } from "./helpers/fake-peer";

const ESCAPES = join(import.meta.dir, "fixtures/escape");
const HOST_DIR = join(import.meta.dir, "..");
const SECRET = "secret-7f3a91";
const sentinelDir = mkdtempSync(join(tmpdir(), "fleet-host-sentinel-"));
const SENTINEL = join(sentinelDir, "sentinel.txt");
writeFileSync(SENTINEL, `sentinel-${SECRET}`);
afterAll(() => rmSync(sentinelDir, { recursive: true, force: true }));

const row = (requestId: string) => ({
  component: "ToolUse",
  sessionId: "ses_test1",
  requestId,
  props: { tool: "bash", rawTool: "Bash", category: "shell", status: "completed", title: "ls", input: { command: "ls" }, inputTruncated: false, output: "", outputTruncated: false },
});

describe("the realm is locked down", () => {
  const kinds: [string, unknown][] = [
    ["function", function () {}],
    ["arrow", () => {}],
    ["async function", async function () {}],
    ["generator", function* () {}],
    ["async generator", async function* () {}],
  ];
  for (const [kind, fn] of kinds) {
    test(`a ${kind}'s constructor can't make code from a string`, () => {
      const ctor = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(fn), "constructor")!.value;
      expect(() => ctor("return 1")).toThrow();
      expect(() => new ctor("return 1")).toThrow();
    });
  }
  test("the global Function is inert and eval is gone", () => {
    expect(() => (globalThis as any).Function("return 1")).toThrow();
    expect((globalThis as any).eval).toBeUndefined();
    expect((() => {}) instanceof Function).toBe(true);
  });
  test("the intrinsics are frozen, so no mod can change them under the host or another mod", () => {
    for (const o of [Object.prototype, Array.prototype, Function.prototype, String.prototype, Number.prototype, Promise.prototype,
      Map.prototype, Set.prototype, RegExp.prototype, Error.prototype, Date.prototype, Symbol.prototype, JSON, Math, Object, Promise]) {
      expect(Object.isFrozen(o)).toBe(true);
    }
    expect(() => {
      (Object.prototype as any).polluted = 1;
    }).toThrow();
    expect(() => {
      (Array.prototype as any).push = () => 0;
    }).toThrow();
    expect(({} as any).polluted).toBeUndefined();
  });
  test("ordinary code still overrides inherited names on its own objects", () => {
    class Problem extends Error {
      constructor(message: string) {
        super(message);
        this.name = "Problem";
      }
    }
    expect(new Problem("x").name).toBe("Problem");
    const o: any = {};
    o.toString = () => "mine";
    o.constructor = "fine";
    expect(String(o)).toBe("mine");
    expect((12345.6).toLocaleString("en-US")).toBe("12,345.6");
  });
});

// These only reach a constructor and report its typeof; the constructor they reach is the inert one (tests above).
const PROBE_ONLY = new Set(["escape-reviver", "escape-tagged", "escape-getproto-literal"]);

describe("the review's escapes fail at run time even past the check", () => {
  // These are refused by the check; load them through a check that lets everything by, to prove the realm stops them.
  for (const name of ["escape-e1", "escape-descriptor", "escape-spawn", "escape-reviver", "escape-tagged", "escape-getproto-literal", "escape-constr-key", "escape-constr-dbg", "escape-fs-read"]) {
    test(name, async () => {
      const peer = new FakePeer();
      createHost({ peer, check: fakeCheck, log: () => {} });
      const loaded = await peer.call("load", { id: `${name}@v1`, name, version: 1, root: join(ESCAPES, name) });
      expect(loaded.hooks.length).toBeGreaterThan(0);
      const event = loaded.hooks[0].event;
      const e = event === "session.start" ? { sessionId: "ses_test1", reason: "start" } : row("call_1");
      const r = await peer.call("dispatch", { event, sessionId: "ses_test1", mods: [`${name}@v1`], e });
      const said = JSON.stringify([peer.notifications, r]);
      expect(said).not.toMatch(/ESCAPED|FILE-READ|OS-HOSTNAME|SPAWN-OUT|PWNED|NOWARN-ENV|escaped:/);
      if (!PROBE_ONLY.has(name)) expect(said).toMatch(/not a valid constructor|THREW: TypeError/);
    });
  }
});

describe("the real host over stdio", () => {
  /** Spawns the real host, sends each request after the previous one is answered, then shuts it down. */
  async function run(mods: string[]) {
    const proc = Bun.spawn([process.execPath, "src/main.ts", "--stdio"], {
      cwd: HOST_DIR,
      stdin: "pipe",
      stdout: "pipe",
      stderr: "pipe",
      env: { FLEET_MOD_SECRET: SECRET, PATH: process.env.PATH ?? "" },
    });
    const lines: any[] = [];
    const waiting = new Map<number, () => void>();
    const reading = (async () => {
      let buffer = "";
      for await (const chunk of proc.stdout as ReadableStream<Uint8Array>) {
        buffer += new TextDecoder().decode(chunk);
        let nl: number;
        while ((nl = buffer.indexOf("\n")) >= 0) {
          const line = JSON.parse(buffer.slice(0, nl));
          buffer = buffer.slice(nl + 1);
          lines.push(line);
          if (line.id !== undefined && !line.method) waiting.get(line.id)?.();
        }
      }
    })();
    let id = 0;
    const call = (method: string, params: unknown) =>
      new Promise<void>((resolve) => {
        waiting.set(++id, resolve);
        proc.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
        void proc.stdin.flush();
      });
    await call("initialize", { protocol: 1, fleetVersion: "test" });
    for (const name of mods) {
      await call("load", { id: `${name}@v1`, name, version: 1, root: join(ESCAPES, name) });
      await call("dispatch", { event: "ui.render", sessionId: "ses_test1", mods: [`${name}@v1`], e: row(`call_${name}`) });
    }
    await call("shutdown", {});
    const [stderr, code] = await Promise.all([new Response(proc.stderr).text(), proc.exited, reading]);
    return { lines, stderr, code };
  }

  test("escapes that pass the check get nothing: no secret, no file, no os", async () => {
    // Fixtures read the sentinel at a fixed path; point the one that does at ours.
    const { lines, stderr, code } = await run(["escape-constr-key", "escape-constr-dbg", "escape-fs-read"]);
    const all = JSON.stringify(lines) + stderr;
    expect(code).toBe(0);
    expect(all).not.toContain(SECRET);
    expect(all).not.toMatch(/ESCAPED|FILE-READ|OS-HOSTNAME/);
    const results = lines.filter((l) => l.result?.failures !== undefined);
    expect(results).toHaveLength(3);
    const logs = lines.filter((l) => l.method === "log").map((l) => l.params.text);
    expect(logs.some((t) => /THREW/.test(t)) || results.some((r) => r.result.failures.length > 0)).toBe(true);
  });

  test("the check refuses E1 before it runs", async () => {
    const { lines } = await run(["escape-e1"]);
    const load = lines.find((l) => l.id === 2);
    expect(load.error.code).toBe(-32001);
    expect(load.error.data.errors.map((e: any) => e.code)).toContain("prototype");
  });

  test("a mod can't reroute the console the host routes to every mod's log", async () => {
    const { lines } = await run(["escape-console"]);
    const r = lines.find((l) => l.id === 3);
    expect(r.result.failures).toHaveLength(1);
    expect(r.result.failures[0].kind).toBe("throw");
  });
});
