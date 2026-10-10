import type { CheckReportHook } from "fleet-mods/protocol-shared";
import { ownerKey } from "./budget";
import { contextFor, inMod } from "./context";
import { matcherToJson } from "./match";
import { EVENTS, type EventName, type HookFn, type HookReg, type LoadedMod, type Runtime } from "./types";

/** Why a module didn't load: becomes an error in the check report. */
export class LoadProblem extends Error {
  constructor(
    readonly code: string,
    message: string,
  ) {
    super(message);
  }
}

export interface LoadSpec {
  id: string;
  name: string;
  version: number | "draft";
  sessionId?: string;
  root: string;
}

const isMatcher = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v);

/** What `load` answers: the hooks `on` registered, in order, matchers as JSON. */
export function hooksOf(mod: LoadedMod): CheckReportHook[] {
  return mod.hooks.map((h) => (h.matcher ? { event: h.event, matcher: matcherToJson(h.matcher) as never } : { event: h.event }));
}

/** Imports the checked JavaScript afresh and runs `register`. Throws a LoadProblem when it can't. */
export async function instantiate(rt: Runtime, spec: LoadSpec, js: string): Promise<LoadedMod> {
  const mod: LoadedMod = {
    id: spec.id,
    name: spec.name,
    version: spec.version,
    sessionId: spec.sessionId,
    root: spec.root,
    hooks: [],
    dead: false,
    starts: new Map(),
    prevStarted: new Set(),
    strikes: 0,
  };
  const ctx = contextFor(rt, spec.id);

  const url = URL.createObjectURL(new Blob([js], { type: "text/javascript" }));
  let register: unknown;
  try {
    const imported = await inMod(ctx, () => import(url));
    register = imported.register;
  } catch (e) {
    throw new LoadProblem("import", e instanceof Error ? e.message : String(e));
  } finally {
    URL.revokeObjectURL(url);
  }
  if (typeof register !== "function") throw new LoadProblem("no-register", "the module doesn't export a register function");

  let open = true;
  const unmatched = new Set<EventName>();
  const on = (event: unknown, a: unknown, b?: unknown) => {
    if (!open) throw new Error("on can only be called inside register, before it returns");
    if (typeof event !== "string" || !(EVENTS as readonly string[]).includes(event)) {
      throw new Error(`unknown event ${JSON.stringify(event)}`);
    }
    const hasMatcher = b !== undefined;
    const matcher = hasMatcher ? a : undefined;
    const hook = hasMatcher ? b : a;
    if (typeof hook !== "function") throw new TypeError(`on(${JSON.stringify(event)}, …) needs a hook function`);
    if (hasMatcher && !isMatcher(matcher)) throw new TypeError(`the matcher for ${event} must be an object`);
    if (!hasMatcher) {
      if (unmatched.has(event as EventName)) throw new Error(`${event} is already hooked without a matcher`);
      unmatched.add(event as EventName);
    }
    const reg: HookReg = { event: event as EventName, matcher: matcher as Record<string, unknown> | undefined, hook: hook as HookFn };
    mod.hooks.push(reg);
    return Object.freeze({
      catch(handler: unknown) {
        if (typeof handler !== "function") throw new TypeError(".catch needs a handler function");
        reg.catchHandler = handler as HookFn;
      },
    });
  };

  try {
    rt.meter.run(ownerKey(spec.id, ""), () => inMod(ctx, () => (register as (on: unknown, options: unknown) => unknown)(on, Object.freeze({}))));
  } catch (e) {
    throw new LoadProblem("register", e instanceof Error ? e.message : String(e));
  } finally {
    open = false;
  }
  return mod;
}
