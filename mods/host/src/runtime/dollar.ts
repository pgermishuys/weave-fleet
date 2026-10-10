import type { Fleet } from "fleet-mods";
import { ownerKey, type Budget } from "./budget";
import { capLine, contextFor, inMod, modContext } from "./context";
import { createElements } from "../tree";
import { toJsonText } from "./freeze";
import { generationOf } from "./runtime";
import type { EventName, LoadedMod, ModContext, Runtime } from "./types";

/** What a `$` needs to know about the call it was made for; the chain updates it as the call goes on. */
export interface DollarScope {
  /** A ui.render hook is running: code in `context` (the hook and its awaits, not its timers or callbacks) is the render. */
  rendering: boolean;
  /** The context the hook runs in. */
  context: ModContext;
  /** ui.render: the site being drawn, which the render's reads subscribe. */
  site?: string;
  /** The budget to pause while a `$` call waits. */
  budget: Budget | null;
}

const PANE_ID = /^[A-Za-z0-9_-]{1,64}$/;

/** `$` for one hook call: bound to a mod, a session and an event. */
export function createDollar(rt: Runtime, mod: LoadedMod, sessionId: string, event: EventName, scope: DollarScope): Fleet {
  const ctx = contextFor(rt, mod.id, sessionId, event);
  const context = { mod: mod.id, sessionId };
  const generation = generationOf(rt, sessionId);
  /** Is this code the render itself? Reads subscribe and writes throw only there. */
  const rendering = () => scope.rendering && modContext.getStore() === scope.context;
  /** The session was forgotten after this `$` was made: it may not leave state or timers behind for it. */
  const stale = () => generationOf(rt, sessionId) !== generation;
  const live = () => {
    if (stale()) throw new Error("the session was forgotten");
  };

  /** Awaits a promise from Fleet with the hook's budget paused. */
  const waiting = async <T>(p: () => Promise<T>): Promise<T> => {
    const budget = scope.budget;
    budget?.pause();
    try {
      return await p();
    } finally {
      budget?.resume();
    }
  };
  const ask = (method: string, params: Record<string, unknown>) =>
    waiting(() => rt.peer.request(method, { ...context, ...params }, { timeoutMs: rt.limits.fleetRequestMs }));
  const sessionField = async (field: string) => ((await ask("session.get", {})) as Record<string, unknown>)[field];
  const owner = {
    mod: mod.id,
    session: sessionId,
    run: <T>(fn: () => T) => rt.meter.run(ownerKey(mod.id, sessionId), () => inMod(ctx, fn)),
    failed: (e: unknown) => rt.fail(mod, sessionId, event, "throw", e instanceof Error ? e.message : String(e)),
    alive: () => !mod.dead,
  };

  const $: Fleet = {
    mod: Object.freeze({ name: mod.name, version: mod.version }),
    ui: {
      resolve: () => createElements(mod.id),
      invalidate(name: unknown) {
        if (name !== "ui.render") throw new TypeError('$.ui.invalidate takes "ui.render"');
        if (!stale()) rt.invalidator.request(mod.id, sessionId);
      },
      async open(pane) {
        if (!pane || typeof pane.id !== "string" || !PANE_ID.test(pane.id)) throw new TypeError("a pane id is 1 to 64 letters, digits, _ or -");
        await ask("ui.open", pane.title === undefined ? { id: pane.id } : { id: pane.id, title: String(pane.title) });
      },
      async close(pane) {
        if (!pane || typeof pane.id !== "string" || !PANE_ID.test(pane.id)) throw new TypeError("a pane id is 1 to 64 letters, digits, _ or -");
        await ask("ui.close", { id: pane.id });
      },
      toast(text, options) {
        if (options !== undefined && (typeof options !== "object" || options === null)) throw new TypeError("toast options must be an object");
        const { timeoutMs, tone } = options ?? {};
        if (timeoutMs !== undefined && !(typeof timeoutMs === "number" && Number.isFinite(timeoutMs) && timeoutMs > 0)) {
          throw new TypeError("toast timeoutMs must be a positive number of milliseconds");
        }
        if (tone !== undefined && tone !== "accent" && tone !== "warn") throw new TypeError('toast tone must be "accent" or "warn"');
        const params: Record<string, unknown> = { ...context, text: String(text).slice(0, rt.limits.toastChars) };
        if (timeoutMs !== undefined) params.timeoutMs = timeoutMs;
        if (tone !== undefined) params.tone = tone;
        rt.peer.request("ui.toast", params, { timeoutMs: rt.limits.fleetRequestMs }).catch((e) => rt.log(`ui.toast failed for ${mod.id}: ${e instanceof Error ? e.message : String(e)}`));
      },
      log(text, options) {
        const level = options?.level === "warn" || options?.level === "error" ? options.level : "info";
        ctx.emit(level, capLine(String(text)));
      },
    },
    state: {
      get(key) {
        if (stale()) return undefined;
        return rt.state.get(mod.name, sessionId, String(key), rendering() ? scope.site : undefined) as never;
      },
      set(key, value) {
        live();
        if (rendering()) throw new Error("a ui.render hook can't write $.state");
        if (rt.state.set(mod.name, sessionId, String(key), value)) rt.invalidator.request(mod.id, sessionId);
      },
    },
    store: {
      async get(key) {
        const r = (await ask("store.get", { key })) as { value?: unknown } | null;
        return r?.value as never;
      },
      async set(key, value) {
        toJsonText(value);
        await ask("store.set", { key, value });
      },
      async delete(key) {
        await ask("store.delete", { key });
      },
      async keys() {
        return ((await ask("store.keys", {})) as { keys: string[] }).keys;
      },
    },
    session: {
      id: async () => sessionId,
      title: async () => (await sessionField("title")) as string,
      harness: async () => (await sessionField("harness")) as string,
      cwd: async () => (await sessionField("cwd")) as string,
      surfaces: async () => (await sessionField("surfaces")) as never,
    },
    clock: {
      now: () => rt.now(),
      after: (ms, fn) => (live(), rt.clock.after(owner, ms, fn)),
      every: (ms, fn) => (live(), rt.clock.every(owner, ms, fn)),
    },
  };
  for (const part of Object.values($)) Object.freeze(part);
  return Object.freeze($);
}
