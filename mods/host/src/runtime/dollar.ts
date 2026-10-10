import type { Fleet } from "fleet-mods";
import type { Budget } from "./budget";
import { capLine, contextFor, inMod } from "./context";
import { createElements } from "../tree";
import { toJsonText } from "./freeze";
import type { EventName, LoadedMod, Runtime } from "./types";

/** What a `$` needs to know about the call it was made for; the chain updates it as the call goes on. */
export interface DollarScope {
  /** A ui.render hook is running: reads subscribe, writes throw. */
  rendering: boolean;
  /** The budget to pause while a `$` call waits. */
  budget: Budget | null;
}

const PANE_ID = /^[A-Za-z0-9_-]{1,64}$/;

/** `$` for one hook call: bound to a mod, a session and an event. */
export function createDollar(rt: Runtime, mod: LoadedMod, sessionId: string, event: EventName, scope: DollarScope): Fleet {
  const ctx = contextFor(rt, mod.id, sessionId, event);
  const context = { mod: mod.id, sessionId };

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
    run: <T>(fn: () => T) => inMod(ctx, fn),
    failed: (e: unknown) => rt.fail(mod.id, sessionId, event, "throw", e instanceof Error ? e.message : String(e)),
    alive: () => !mod.dead,
  };

  const $: Fleet = {
    mod: Object.freeze({ name: mod.name, version: mod.version }),
    ui: {
      resolve: () => createElements(mod.id),
      invalidate(name: unknown) {
        if (name !== "ui.render") throw new TypeError('$.ui.invalidate takes "ui.render"');
        rt.invalidator.request(mod.id, sessionId);
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
        const params: Record<string, unknown> = { ...context, text: String(text).slice(0, rt.limits.toastChars) };
        if (options?.timeoutMs !== undefined) params.timeoutMs = options.timeoutMs;
        if (options?.tone !== undefined) params.tone = options.tone;
        rt.peer.request("ui.toast", params, { timeoutMs: rt.limits.fleetRequestMs }).catch((e) => rt.log(`ui.toast failed for ${mod.id}: ${e instanceof Error ? e.message : String(e)}`));
      },
      log(text, options) {
        const level = options?.level === "warn" || options?.level === "error" ? options.level : "info";
        ctx.emit(level, capLine(String(text)));
      },
    },
    state: {
      get(key) {
        return rt.state.get(mod.id, sessionId, String(key), scope.rendering) as never;
      },
      set(key, value) {
        if (scope.rendering) throw new Error("a ui.render hook can't write $.state");
        if (rt.state.set(mod.id, sessionId, String(key), value)) rt.invalidator.request(mod.id, sessionId);
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
      after: (ms, fn) => rt.clock.after(owner, ms, fn),
      every: (ms, fn) => rt.clock.every(owner, ms, fn),
    },
  };
  for (const part of Object.values($)) Object.freeze(part);
  return Object.freeze($);
}
