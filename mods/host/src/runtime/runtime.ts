import { LIMITS, type HostLimits } from "../limits";
import { createClock } from "./clock";
import { createHandles } from "./handles";
import { createInvalidator } from "./invalidate";
import { createStateStore } from "./state";
import type { EventName, ModId, Peer, Runtime } from "./types";

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
    strikes: new Map(),
    state: createStateStore(() => limits.stateBytes),
    invalidator: createInvalidator(peer, () => limits.invalidatePerSecond, options.now),
    clock: createClock(() => limits),
    handles: createHandles(),
    calls: new Set(),
    inflight: new Set(),
    background: new Set(),

    fail(modId: ModId, sessionId: string, event: EventName, kind, message) {
      if (!rt.mods.has(modId)) return;
      const strikes = (rt.strikes.get(modId) ?? 0) + 1;
      rt.strikes.set(modId, strikes);
      peer.notify("failed", { mod: modId, event, kind, message, strikes, sessionId });
      if (strikes >= limits.strikes) rt.unloadMod(modId);
    },

    unloadMod(id: ModId) {
      const mod = rt.mods.get(id);
      if (mod) {
        mod.dead = true;
        rt.mods.delete(id);
      }
      stopModWork(rt, id);
      rt.state.dropMod(id);
      rt.strikes.delete(id);
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

/** `forget`: everything the host holds for a session. */
export function forgetSession(rt: Runtime, sessionId: string): void {
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
