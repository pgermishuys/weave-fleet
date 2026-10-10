import { realpathSync, statSync } from "node:fs";
import { isAbsolute, relative, resolve } from "node:path";
import type { RenderComponent } from "fleet-mods";
import { toWire } from "../tree";
import { ownerKey, type Budget } from "./budget";
import { contextFor, inMod } from "./context";
import { createDollar, type DollarScope } from "./dollar";
import type { HandleEntry } from "./handles";
import { frozenCopy } from "./freeze";
import { generationOf } from "./runtime";
import { WATCH_ONLY, type ActiveCall, type EventName, type HookFailureReport, type HookReg, type LoadedMod, type Runtime } from "./types";

/** What a chain step resolves to. `by` names the mod whose hook answered `v`; `nullBy` the mod that answered `null` at ui.render. */
export interface Out {
  v: unknown;
  by?: string;
  nullBy?: string;
}

export interface HookRef {
  mod: LoadedMod;
  reg: HookReg;
}

/** One dispatch being run. */
export interface Dispatch {
  rt: Runtime;
  event: EventName;
  sessionId: string;
  hooks: HookRef[];
  failures: HookFailureReport[];
  /** ui.render: the site being drawn. */
  component?: RenderComponent;
  /** ui.render: the site with its request, as handles and state subscriptions know it. */
  site?: string;
  /** ui.press/input/select: the callback the chain ends in. */
  control?: HandleEntry;
  /** ui.render: the mod whose accepted answer held each element that has no OWNER tag (one written by hand). */
  owners: WeakMap<object, string>;
  /** The session's generation when the dispatch began. */
  generation: number;
}

/** Was the dispatch's session forgotten since it began? Then nothing more runs for it. */
export const isStale = (d: Dispatch) => generationOf(d.rt, d.sessionId) !== d.generation;

const FLEET = Object.freeze({ type: "Fleet" });
const messageOf = (e: unknown) => (e instanceof Error ? e.message : String(e));
const isObject = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v);

/** Does `path` name an .html file inside `owner`'s folder (after following links)? */
export function pageExists(rt: Runtime, owner: string, path: string): boolean {
  const root = rt.mods.get(owner)?.root;
  if (!root || !/\.html?$/i.test(path) || isAbsolute(path)) return false;
  try {
    const realRoot = realpathSync(root);
    const full = realpathSync(resolve(root, path));
    const rel = relative(realRoot, full);
    return rel !== "" && !rel.startsWith("..") && !isAbsolute(rel) && statSync(full).isFile();
  } catch {
    return false;
  }
}

/**
 * Why `v` isn't an answer the event takes, or null. When it is one, elements in it that no earlier answer owned become
 * `mod`'s, so a hand-written element belongs to the mod whose hook returned it.
 */
function accept(d: Dispatch, mod: LoadedMod, v: unknown): string | null {
  switch (d.event) {
    case "ui.render": {
      if (v === undefined) return "the hook returned nothing; return a tree, null or await next(e)";
      const unowned: object[] = [];
      const w = toWire(v, {
        site: d.component!,
        defaultOwner: mod.id,
        ownerOf: (el) => d.owners.get(el) ?? void unowned.push(el),
        pageExists: (o, p) => pageExists(d.rt, o, p),
        allocHandle: () => "h0",
        limits: d.rt.limits,
      });
      if (!w.ok) return w.reason;
      for (const el of unowned) d.owners.set(el, mod.id);
      return null;
    }
    case "ui.press":
      return isObject(v) && typeof v.element === "string" ? null : "ui.press takes next(e) or { element }";
    case "ui.input":
    case "ui.select":
      return isObject(v) && typeof v.element === "string" && typeof v.value === "string" ? null : `${d.event} takes next(e) or { element, value }`;
    default:
      return null;
  }
}

// ─── next ─────────────────────────────────────────────────────────────────────────────────────────────────────────

interface NextState {
  called: boolean;
  resolved: boolean;
  value?: Out;
  /** The latest call to next, settled or not. */
  last?: Promise<Out>;
  over: boolean;
}

type Rest = (e: any) => Promise<Out>;

function makeNext(st: NextState, rest: Rest, budget: Budget, signal: AbortSignal, event: EventName, ms: number) {
  const next = (e2: unknown) => {
    if (st.over) return Promise.reject(new Error("the dispatch is over"));
    let copy: unknown;
    try {
      copy = frozenCopy(e2);
    } catch (err) {
      return Promise.reject(err);
    }
    st.called = true;
    budget.pause();
    const p = rest(copy)
      .then((out) => {
        st.resolved = true;
        st.value = out;
        return out;
      })
      .finally(() => budget.resume());
    st.last = p;
    return p.then((o) => o.v);
  };
  Object.defineProperties(next, {
    signal: { value: signal },
    event: { value: event },
    budget: {
      value: Object.freeze({
        ms,
        get remainingMs() {
          return budget.remainingMs;
        },
      }),
    },
  });
  return next;
}

// ─── Running one handler ──────────────────────────────────────────────────────────────────────────────────────────

type Outcome = { value: unknown } | { error: unknown } | { timeout: true } | { aborted: true };

/**
 * Runs `call` under the budget `makeBudget` builds: resolves with its outcome, `timeout` when the budget runs out
 * first, or `aborted` at once when `killed` fires (an unload, forget or reload). Either aborts `ac` (next.signal).
 */
function execute(
  ac: AbortController,
  killed: AbortSignal,
  makeBudget: (expire: () => void) => Budget,
  call: () => unknown,
): { done: Promise<Outcome>; budget: Budget } {
  let settle!: (o: Outcome) => void;
  const done = new Promise<Outcome>((r) => (settle = r));
  const budget = makeBudget(() => {
    settle({ timeout: true });
    ac.abort();
  });
  const kill = () => {
    settle({ aborted: true });
    ac.abort();
  };
  if (killed.aborted) kill();
  else killed.addEventListener("abort", kill, { once: true });
  void done.then(() => killed.removeEventListener("abort", kill));
  try {
    Promise.resolve(call()).then(
      (value) => settle({ value }),
      (error) => settle({ error }),
    );
  } catch (error) {
    settle({ error });
  }
  return { done, budget };
}

function nullAttribution(mod: LoadedMod, value: unknown, st: NextState): string | undefined {
  if (value !== null) return undefined;
  return st.resolved && st.value?.v === null && st.value.nullBy ? st.value.nullBy : mod.id;
}

/** Calls one hook with the rest of the chain behind it. Never rejects. */
async function callHook(d: Dispatch, ref: HookRef, e: any, rest: Rest): Promise<Out> {
  const { rt } = d;
  const { mod, reg } = ref;
  const sid = d.sessionId;
  const ac = new AbortController();
  const killed = new AbortController();
  const active: ActiveCall = { modId: mod.id, sessionId: sid, abort: () => killed.abort() };
  rt.calls.add(active);
  const ctx = contextFor(rt, mod.id, sid, d.event);
  const scope: DollarScope = { rendering: d.event === "ui.render", context: ctx, site: d.site, budget: null };
  const $ = createDollar(rt, mod, sid, d.event, scope);
  const st: NextState = { called: false, resolved: false, over: false };
  const hookMs = rt.limits.hookMs;
  const key = ownerKey(mod.id, sid);
  let replay: Promise<Out> | undefined;
  /** The rest of the chain for the hook and its `.catch` together: what the hook's next got, else one run of it. */
  const restOnce: Rest = async (e2) => {
    if (st.called) {
      if (st.last) await st.last.catch(() => {});
      if (st.resolved) return st.value!;
    }
    return (replay ??= rest(e2));
  };

  try {
    let next!: ReturnType<typeof makeNext>;
    const run = execute(
      ac,
      killed.signal,
      (expire) => {
        const b = rt.meter.budget(hookMs, expire, key);
        scope.budget = b;
        next = makeNext(st, rest, b, ac.signal, d.event, hookMs);
        return b;
      },
      () => rt.meter.run(key, () => inMod(ctx, () => reg.hook($, e, next))),
    );
    const outcome = await run.done;
    run.budget.finish();
    st.over = true;
    // An aborted call is skipped: no failure, and no strike for a module that is gone or replaced.
    if ("aborted" in outcome) return await restOnce(e);

    let failure: { kind: "throw" | "timeout"; message: string } | null = null;
    if ("timeout" in outcome) failure = { kind: "timeout", message: `the hook used more than its ${hookMs} ms` };
    else if ("error" in outcome) failure = { kind: "throw", message: messageOf(outcome.error) };
    else {
      const problem = accept(d, mod, outcome.value);
      if (problem) failure = { kind: "throw", message: problem };
    }

    if (!failure) {
      mod.strikes = 0;
      const value = (outcome as { value: unknown }).value;
      if (WATCH_ONLY.has(d.event)) return await restOnce(e);
      return { v: value, by: mod.id, nullBy: nullAttribution(mod, value, st) };
    }

    if (reg.catchHandler) {
      const answered = await runCatch(d, ref, e, restOnce, st, failure, scope, $, ctx, killed.signal);
      if (answered) return WATCH_ONLY.has(d.event) ? await restOnce(e) : answered;
    }

    const strikes = killed.signal.aborted ? null : rt.strike(mod);
    if (strikes !== null) d.failures.push({ mod: mod.id, event: d.event, kind: failure.kind, message: failure.message, strikes });
    return await restOnce(e);
  } finally {
    scope.rendering = false;
    scope.budget = null;
    rt.calls.delete(active);
  }
}

/** Runs the hook's `.catch` handler, whose next is `restOnce`. Returns its answer, or null when it has none. */
async function runCatch(
  d: Dispatch,
  ref: HookRef,
  e: any,
  restOnce: Rest,
  hookSt: NextState,
  failure: { kind: "throw" | "timeout"; message: string },
  scope: DollarScope,
  $: any,
  ctx: ReturnType<typeof contextFor>,
  killed: AbortSignal,
): Promise<Out | null> {
  const { rt } = d;
  const { mod, reg } = ref;
  const catchMs = rt.limits.catchMs;
  const ac = new AbortController();
  const st: NextState = { called: false, resolved: false, over: false };

  let catchNext!: ReturnType<typeof makeNext> & { error?: unknown; called?: boolean };

  const run = execute(
    ac,
    killed,
    (expire) => {
      const b = rt.meter.budget(catchMs, expire, ownerKey(mod.id, d.sessionId));
      scope.budget = b;
      catchNext = makeNext(st, restOnce, b, ac.signal, d.event, catchMs);
      Object.defineProperties(catchNext, {
        error: { value: Object.freeze({ kind: failure.kind, message: failure.message }) },
        called: { get: () => hookSt.called },
      });
      return b;
    },
    () => rt.meter.run(ownerKey(mod.id, d.sessionId), () => inMod(ctx, () => reg.catchHandler!($, e, catchNext))),
  );
  const outcome = await run.done;
  run.budget.finish();
  st.over = true;
  if (!("value" in outcome)) return null;
  if (accept(d, mod, outcome.value) !== null) return null;
  return { v: outcome.value, by: mod.id, nullBy: nullAttribution(mod, outcome.value, hookSt) };
}

// ─── The chain ────────────────────────────────────────────────────────────────────────────────────────────────────

/** Runs the chain from hook `i` with `e`. */
export function runFrom(d: Dispatch, i: number, e: any): Promise<Out> {
  let j = i;
  while (j < d.hooks.length && d.hooks[j]!.mod.dead) j++;
  if (j >= d.hooks.length || isStale(d)) return endOfChain(d, e);
  return callHook(d, d.hooks[j]!, e, (e2) => runFrom(d, j + 1, e2));
}

/** What Fleet does when no hook is left to answer. */
async function endOfChain(d: Dispatch, e: any): Promise<Out> {
  switch (d.event) {
    case "ui.render":
      return { v: FLEET };
    case "session.start":
    case "turn.complete":
      return { v: e };
  }
  const h = d.control!;
  const { rt } = d;
  const owner = rt.mods.get(h.owner);
  const answer = { v: d.event === "ui.press" ? { element: e.element } : { element: e.element, value: e.value } };
  if (isStale(d)) return answer;
  const ctx = contextFor(rt, h.owner, d.sessionId, d.event);
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timeout = new Promise<"timeout">((r) => (timer = setTimeout(() => r("timeout"), rt.limits.hookMs)));
  try {
    const args = h.kind === "onPress" ? [] : [e.value];
    const winner = await Promise.race([Promise.resolve(rt.meter.run(ownerKey(h.owner, d.sessionId), () => inMod(ctx, () => h.fn(...args)))).then(() => "done" as const), timeout]);
    if (winner === "timeout" && owner) rt.fail(owner, d.sessionId, d.event, "timeout", `the callback used more than its ${rt.limits.hookMs} ms`);
  } catch (err) {
    if (owner) rt.fail(owner, d.sessionId, d.event, "throw", messageOf(err));
  } finally {
    clearTimeout(timer);
  }
  return answer;
}
