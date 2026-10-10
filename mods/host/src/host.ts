/**
 * The mod host: answers Fleet's JSON-RPC methods (docs/mods/api.md, "The protocol") over a `Peer`.
 */
import { join } from "node:path";
import type { CheckReport } from "fleet-mods/protocol";
import { checkMod } from "./check";
import type { HostLimits } from "./limits";
import { ErrorCodes, RpcError } from "./rpc";
import { forgetSession, createRuntime, stopModWork } from "./runtime/runtime";
import { installConsole } from "./runtime/context";
import { dispatch, ensureStarted } from "./runtime/dispatch";
import { hooksOf, instantiate, LoadProblem } from "./runtime/registry";
import type { LoadedMod, Peer, Runtime } from "./runtime/types";
import { HOST_VERSION, PROTOCOL } from "./version";

export type { Peer };

export interface HostOptions {
  peer: Peer;
  limits?: Partial<HostLimits>;
  /** The static check. Default: the real one. */
  check?: typeof checkMod;
  /** Default: process.exit. */
  exit?: (code: number) => void;
  /** Diagnostics. Default: stderr. */
  log?: (message: string) => void;
  now?: () => number;
}

export interface Host {
  /** Waits for work that follows an answer (the session.start runs after a reload). */
  idle(): Promise<void>;
  /** Ids of the modules loaded now. */
  loaded(): string[];
  /** Stops timers and pending work without exiting. */
  close(): void;
}

const bad = (message: string) => new RpcError(ErrorCodes.invalidParams, message);
const ID = /^([a-z][a-z0-9-]*)@(?:v(\d+)|draft:(.+))$/;
const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

function parseLoad(p: any): { id: string; name: string; version: number | "draft"; sessionId?: string; root: string } {
  if (typeof p !== "object" || p === null) throw bad("load takes an object");
  const { id, name, version, sessionId, root } = p;
  if (typeof id !== "string" || typeof name !== "string" || typeof root !== "string") throw bad("load needs id, name and root");
  const m = ID.exec(id);
  if (!m) throw bad(`id ${JSON.stringify(id)} is neither name@v<n> nor name@draft:<sessionId>`);
  if (m[1] !== name) throw bad(`id ${id} doesn't match the name ${name}`);
  if (m[2] !== undefined) {
    if (version !== Number(m[2])) throw bad(`id ${id} doesn't match version ${JSON.stringify(version)}`);
    if (sessionId !== undefined) throw bad("a kept version takes no sessionId");
    return { id, name, version: Number(m[2]), root };
  }
  if (version !== "draft") throw bad(`id ${id} is a draft, so version must be "draft"`);
  if (sessionId !== m[3]) throw bad(`id ${id} doesn't match sessionId ${JSON.stringify(sessionId)}`);
  return { id, name, version: "draft", sessionId: m[3], root };
}

/** Wires the host's methods onto `options.peer`. */
export function createHost(options: HostOptions): Host {
  const { peer } = options;
  const check = options.check ?? checkMod;
  const exit = options.exit ?? ((code: number) => process.exit(code));
  const log = options.log ?? ((m: string) => void process.stderr.write(`${m}\n`));
  const rt: Runtime = createRuntime({ peer, limits: options.limits, log, now: options.now ?? Date.now });
  installConsole();
  let pendingReloads = 0;

  const fail = (name: string, report: CheckReport, code: string, message: string): never => {
    const withError: CheckReport = { ...report, ok: false, errors: [...report.errors, { code, message }] };
    throw new RpcError(ErrorCodes.notLoaded, `${name} doesn't load: ${message}`, withError);
  };

  peer.handle("initialize", (p) => {
    if (p?.protocol !== PROTOCOL) throw new RpcError(ErrorCodes.protocol, `protocol ${JSON.stringify(p?.protocol)} isn't supported`, { supported: [PROTOCOL] });
    return { protocol: PROTOCOL, hostVersion: HOST_VERSION, bunVersion: Bun.version };
  });

  peer.handle("check", async (p) => {
    if (typeof p?.root !== "string") throw bad("check needs a root");
    return (await check(p.root, typeof p.manifest === "string" ? p.manifest : undefined, rt.limits)).report;
  });

  peer.handle("load", async (p) => {
    const spec = parseLoad(p);
    const checked = await check(spec.root, join(spec.root, "mod.json"), rt.limits);
    const report = checked.report;
    if (!report.ok || checked.js === undefined) {
      throw new RpcError(ErrorCodes.notLoaded, `${spec.name} doesn't load: ${report.errors[0]?.message ?? "the check failed"}`, report);
    }
    if (report.name !== spec.name) fail(spec.name, report, "name-mismatch", `mod.json names the mod ${JSON.stringify(report.name)}, not ${JSON.stringify(spec.name)}`);
    let mod: LoadedMod;
    try {
      mod = await instantiate(rt, spec, checked.js);
    } catch (e) {
      if (e instanceof LoadProblem) return fail(spec.name, report, e.code, e.message);
      throw e;
    }

    const old = rt.mods.get(spec.id);
    if (old) {
      old.dead = true;
      mod.prevStarted = new Set(old.starts.keys());
      stopModWork(rt, spec.id);
      rt.strikes.delete(spec.id);
    }
    rt.mods.set(spec.id, mod);
    if (mod.prevStarted.size > 0) scheduleReloadStarts(mod);
    return { check: report, hooks: hooksOf(mod) };
  });

  /** After `load` has answered: the new module starts the sessions the old one had. */
  function scheduleReloadStarts(mod: LoadedMod): void {
    pendingReloads++;
    setTimeout(async () => {
      try {
        for (const sid of [...mod.prevStarted]) {
          if (rt.mods.get(mod.id) !== mod) break;
          for (const f of await ensureStarted(rt, mod, sid)) peer.notify("failed", { ...f, sessionId: sid });
        }
      } catch (e) {
        log(`reload start of ${mod.id} failed: ${e instanceof Error ? e.message : String(e)}`);
      } finally {
        pendingReloads--;
      }
    }, 0);
  }

  peer.handle("unload", (p) => {
    if (typeof p?.id === "string") rt.unloadMod(p.id);
    return {};
  });

  peer.handle("dispatch", (p) => dispatch(rt, p));

  peer.handle("forget", (p) => {
    if (typeof p?.sessionId !== "string") throw bad("forget needs a sessionId");
    forgetSession(rt, p.sessionId);
    return {};
  });

  function stopTimers(): void {
    rt.clock.stopAll();
    rt.invalidator.stopAll();
  }

  peer.handle("shutdown", () => {
    setTimeout(async () => {
      stopTimers();
      await Promise.race([Promise.allSettled([...rt.inflight, ...rt.background]), sleep(rt.limits.shutdownMs)]);
      exit(0);
    }, 0);
    return {};
  });

  return {
    async idle() {
      while (pendingReloads > 0 || rt.background.size > 0) await sleep(1);
    },
    loaded: () => [...rt.mods.keys()],
    close: stopTimers,
  };
}
