import { mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createHost } from "../../src/host";
import type { HostLimits } from "../../src/limits";
import { check } from "./check";
import { FakePeer } from "./fake-peer";

const base = mkdtempSync(join(tmpdir(), "fleet-host-test-"));
let counter = 0;

/** Writes a mod (folder named after it) and returns its root. `body` goes inside `register = (on) => { … }`. */
export function writeMod(name: string, body: string, extra: Record<string, string> = {}): string {
  const root = join(base, `m${++counter}`, name);
  mkdirSync(root, { recursive: true });
  writeFileSync(
    join(root, "mod.json"),
    JSON.stringify({ name, version: "0.1.0", description: "A test mod", hooks: "./mod.ts" }),
  );
  writeFileSync(join(root, "mod.ts"), `export const register = (on) => {\n${body}\n};\n`);
  for (const [path, text] of Object.entries(extra)) writeFileSync(join(root, path), text);
  return root;
}

export const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

/** Waits until `cond()` is true, up to a second. */
export async function until(cond: () => boolean, what = "condition"): Promise<void> {
  const end = Date.now() + 1000;
  while (!cond()) {
    if (Date.now() > end) throw new Error(`timed out waiting for ${what}`);
    await sleep(2);
  }
}

export const TOOL_ROW = {
  tool: "bash",
  rawTool: "Bash",
  category: "shell",
  status: "completed",
  title: "dotnet test",
  input: { command: "dotnet test" },
  inputTruncated: false,
  output: "ok",
  outputTruncated: false,
};

export function renderE(component = "ToolUse", requestId = "call_1", props: any = TOOL_ROW, sessionId = "ses_test1") {
  return { component, sessionId, requestId, props };
}

export interface Setup {
  peer: FakePeer;
  host: ReturnType<typeof createHost>;
  exits: number[];
  logs: string[];
  /** Writes and loads a kept mod `name@v1` (or the given id). */
  load(name: string, body: string, o?: { id?: string; version?: number | "draft"; sessionId?: string }): Promise<any>;
  /** Dispatches a ui.render for `mods`. */
  render(mods: string[], e?: any, sessionId?: string): Promise<any>;
  dispatch(event: string, mods: string[], e: any, sessionId?: string): Promise<any>;
}

export function setup(limits: Partial<HostLimits> = {}, checkOverride?: typeof check): Setup {
  const peer = new FakePeer();
  const exits: number[] = [];
  const logs: string[] = [];
  const host = createHost({ peer, limits, check: checkOverride ?? check, exit: (c) => void exits.push(c), log: (m) => void logs.push(m) });
  const s: Setup = {
    peer,
    host,
    exits,
    logs,
    async load(name, body, o = {}) {
      const version = o.version ?? 1;
      const id = o.id ?? (version === "draft" ? `${name}@draft:${o.sessionId}` : `${name}@v${version}`);
      const root = writeMod(name, body);
      return peer.call("load", { id, name, version, sessionId: o.sessionId, root });
    },
    render(mods, e = renderE(), sessionId = "ses_test1") {
      return peer.call("dispatch", { event: "ui.render", sessionId, e, mods });
    },
    dispatch(event, mods, e, sessionId = "ses_test1") {
      return peer.call("dispatch", { event, sessionId, e, mods });
    },
  };
  return s;
}
