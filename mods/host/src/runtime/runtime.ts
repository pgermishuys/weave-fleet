import { LIMITS, type HostLimits } from "../limits";
import { createMeter } from "./budget";
import { createClock } from "./clock";
import { createHandles } from "./handles";
import { createInvalidator } from "./invalidate";
import { createStateStore } from "./state";
import type { EventName, LoadedMod, ModId, Peer, Runtime } from "./types";

export interface RuntimeOptions {
  peer: Peer;
  limits?: Partial<HostLimits>;
  log: (message: string) => void;
  now: () => number;
}

/** The host's shared state, and the two things many parts need: counting a failure, and stopping a module. */
export function createRuntime(options: RuntimeOptions): Runtime {
  const limits: HostLimits = { ...LIMITS, ...options.limits };
  const { peer } = options;

  const rt: Runtime = {
    peer,
    limits,
    log: options.log,
    now: options.now,
    mods: new Map(),
    state: createStateStore(() => limits.stateBytes),
    invalidator: createInvalidator(peer, () => limits.invalidatePerSecond, options.now),
    clock: createClock(() => limits),
    handles: createHandles(),
    meter: createMeter(options.now),
    calls: new Set(),
    generations: new Map(),
    background: new Set(),

    fail(mod: LoadedMod, sessionId: string, event: EventName, kind, message) {
      const strikes = rt.strike(mod);
      if (strikes !== null) peer.notify("failed", { mod: mod.id, event, kind, message, strikes, sessionId });
    },

    strike(mod: LoadedMod) {
      if (rt.mods.get(mod.id) !== mod || mod.dead) return null;
      const strikes = ++mod.strikes;
      if (strikes >= limits.strikes) rt.unloadMod(mod.id);
      return strikes;
    },

    unloadMod(id: ModId) {
      const mod = rt.mods.get(id);
      if (mod) {
        mod.dead = true;
        rt.mods.delete(id);
      }
      stopModWork(rt, id);
      // $.state stays: it is the name's, per session, and only `forget` (or the host stopping) drops it, so Keep and
      // Undo keep it whatever order Fleet loads and unloads the versions in.
    },
  };
  return rt;
}

/** Everything a module owns that dies with it: calls in flight, timers, handles, pending invalidations. */
export function stopModWork(rt: Runtime, id: ModId): void {
  for (const c of [...rt.calls]) if (c.modId === id) c.abort();
  rt.clock.stopMod(id);
  rt.handles.dropMod(id);
  rt.invalidator.dropMod(id);
}

/** The session's generation: a `$` or dispatch that captured an older one outlived a `forget`. */
export const generationOf = (rt: Runtime, sessionId: string): number => rt.generations.get(sessionId) ?? 0;

/** `forget`: everything the host holds for a session. */
export function forgetSession(rt: Runtime, sessionId: string): void {
  rt.generations.set(sessionId, generationOf(rt, sessionId) + 1);
  for (const c of [...rt.calls]) if (c.sessionId === sessionId) c.abort();
  rt.state.dropSession(sessionId);
  rt.clock.stopSession(sessionId);
  rt.handles.dropSession(sessionId);
  rt.invalidator.dropSession(sessionId);
  for (const mod of rt.mods.values()) {
    mod.starts.delete(sessionId);
    mod.prevStarted.delete(sessionId);
  }
}
