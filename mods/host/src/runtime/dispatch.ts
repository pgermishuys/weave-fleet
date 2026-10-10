import { RpcError, ErrorCodes } from "../rpc";
import { isStale, runFrom, pageExists, type Dispatch, type HookRef } from "./chain";
import { generationOf } from "./runtime";
import { frozenCopy } from "./freeze";
import { matches } from "./match";
import { toWire } from "../tree";
import { CONTROL_EVENTS, EVENTS, type EventName, type HookFailureReport, type LoadedMod, type Runtime, type Surface } from "./types";
import type { RenderComponent } from "fleet-mods";

export interface DispatchParams {
  event: EventName;
  sessionId: string;
  e: Record<string, unknown>;
  mods: string[];
  surface?: Surface;
}

const KIND_OF: Record<string, (e: Record<string, unknown>) => string> = {
  "ui.press": () => "onPress",
  "ui.input": (e) => (e.kind === "submit" ? "onSubmit" : "onInput"),
  "ui.select": () => "onSelect",
};

const bad = (message: string) => new RpcError(ErrorCodes.invalidParams, message);
const isObject = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v);

function hooksFor(mods: LoadedMod[], event: EventName, e: unknown): HookRef[] {
  const refs: HookRef[] = [];
  for (const mod of mods) for (const reg of mod.hooks) if (reg.event === event && matches(reg.matcher, e)) refs.push({ mod, reg });
  return refs;
}

function newDispatch(rt: Runtime, event: EventName, sessionId: string, hooks: HookRef[], announce: boolean): Dispatch {
  return { rt, event, sessionId, hooks, failures: [], owners: new WeakMap(), generation: generationOf(rt, sessionId), announce };
}

/**
 * Runs the `session.start` hooks of `mod` for the session if no earlier run has; shares a run that is going on.
 * Returns the failures of the run this call made (none when it shared or found it done).
 * `announce`: the dispatch this run comes before has several mods, so its hooks are announced with `running`.
 */
export async function ensureStarted(rt: Runtime, mod: LoadedMod, sessionId: string, announce = false): Promise<HookFailureReport[]> {
  const existing = mod.starts.get(sessionId);
  if (existing) {
    await existing;
    return [];
  }
  const e = { sessionId, reason: mod.prevStarted.has(sessionId) ? "reload" : "start" };
  const run = (async () => {
    const hooks = hooksFor([mod], "session.start", e);
    if (hooks.length === 0) return [];
    const d = newDispatch(rt, "session.start", sessionId, hooks, announce);
    await runFrom(d, 0, frozenCopy(e));
    return d.failures;
  })();
  mod.starts.set(sessionId, run);
  return run;
}

/** `dispatch`: runs the chain for one event and answers `{ result, drawnBy?, failures }`. */
export async function dispatch(rt: Runtime, params: DispatchParams) {
  if (!isObject(params)) throw bad("dispatch takes an object");
  const { event, sessionId, e, mods } = params;
  if (!(EVENTS as readonly string[]).includes(event as string)) throw bad(`unknown event ${JSON.stringify(event)}`);
  if (typeof sessionId !== "string" || sessionId === "") throw bad("sessionId must be a string");
  if (!isObject(e)) throw bad("e must be an object");
  if (!Array.isArray(mods) || mods.some((m) => typeof m !== "string")) throw bad("mods must be a list of ids");
  if (event === "ui.render" && typeof e.component !== "string") throw bad("a ui.render event needs a component");

  let control;
  let hookE: Record<string, unknown> = e;
  if (CONTROL_EVENTS.has(event)) {
    const handle = typeof e.handle === "string" ? rt.handles.get(e.handle) : undefined;
    if (!handle || handle.sessionId !== sessionId || handle.kind !== KIND_OF[event]!(e)) {
      throw new RpcError(ErrorCodes.invalidParams, "unknown or expired handle");
    }
    control = handle;
    // Who drew the control and which one it is come from the handle, not from what Fleet sent.
    const { handle: _dropped, ...rest } = e;
    hookE = { ...rest, mod: handle.owner.slice(0, handle.owner.indexOf("@")), element: handle.key };
  }

  const chainMods: LoadedMod[] = [];
  for (const id of new Set(mods)) {
    const m = rt.mods.get(id);
    if (m && !m.dead && (m.sessionId === undefined || m.sessionId === sessionId)) chainMods.push(m);
  }

  const failures: HookFailureReport[] = [];
  let scope = chainMods;
  if (event === "session.start") {
    scope = chainMods.filter((m) => !m.starts.has(sessionId));
    const d = newDispatch(rt, event, sessionId, hooksFor(scope, event, hookE), chainMods.length > 1);
    let release!: (f: HookFailureReport[]) => void;
    const started = new Promise<HookFailureReport[]>((r) => (release = r));
    for (const m of scope) m.starts.set(sessionId, started);
    try {
      await runFrom(d, 0, frozenCopy(hookE));
    } finally {
      release(d.failures);
    }
    return { result: hookE, failures: d.failures };
  }

  const generation = generationOf(rt, sessionId);
  for (const m of chainMods) {
    if (generationOf(rt, sessionId) !== generation) break;
    failures.push(...(await ensureStarted(rt, m, sessionId, chainMods.length > 1)));
  }

  const live = chainMods.filter((m) => !m.dead);
  const d = newDispatch(rt, event, sessionId, hooksFor(live, event, hookE), chainMods.length > 1);
  d.generation = generation;
  d.control = control;
  if (event === "ui.render") {
    d.component = hookE.component as RenderComponent;
    d.site = `${d.component}\u0000${hookE.requestId}`;
    for (const m of live) rt.state.resetSite(m.name, sessionId, d.site);
  }
  const out = await runFrom(d, 0, frozenCopy(hookE));
  failures.push(...d.failures);

  if (event !== "ui.render") return { result: out.v, failures };
  // Forgotten while it ran: Fleet no longer draws the session, so no handles are kept for it.
  if (isStale(d)) return { result: { type: "Fleet" }, failures };

  const site = d.site!;
  const previous = rt.handles.ofSite(sessionId, site);
  const wire = toWire(out.v, {
    site: d.component!,
    defaultOwner: out.by ?? "",
    ownerOf: (el) => d.owners.get(el),
    pageExists: (o, p) => pageExists(rt, o, p),
    allocHandle: (c) => rt.handles.alloc({ ...c, sessionId, site }),
    limits: rt.limits,
  });
  rt.handles.drop(previous);
  if (!wire.ok) {
    rt.log(`final tree for ${d.component} was invalid: ${wire.reason}`);
    return { result: { type: "Fleet" }, failures };
  }
  const drawnBy = wire.tree === null ? [out.nullBy].filter((x): x is string => !!x) : wire.drawnBy;
  return drawnBy.length > 0 ? { result: wire.tree, drawnBy, failures } : { result: wire.tree, failures };
}
