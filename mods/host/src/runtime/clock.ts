import type { Timer } from "fleet-mods";
import type { HostLimits } from "../limits";

interface Live {
  mod: string;
  session: string;
  stop: () => void;
}

export interface Clock {
  after(owner: Owner, ms: number, fn: () => void | Promise<void>): Timer;
  every(owner: Owner, ms: number, fn: () => void | Promise<void>): Timer;
  stopMod(mod: string): void;
  stopSession(session: string): void;
  stopAll(): void;
}

/** Who a timer belongs to, and how its failures are reported. */
export interface Owner {
  mod: string;
  session: string;
  /** Runs `fn` as the mod's code. */
  run: <T>(fn: () => T) => T;
  /** Called with the error when `fn` throws or rejects. */
  failed: (error: unknown) => void;
  /** Is the module still the loaded one? */
  alive: () => boolean;
}

/** `$.clock` timers, owned by a mod id and a session. */
export function createClock(limits: () => HostLimits): Clock {
  const live = new Set<Live>();

  const count = (o: Owner) => [...live].filter((t) => t.mod === o.mod && t.session === o.session).length;

  const start = (o: Owner, ms: number, fn: () => void | Promise<void>, repeat: boolean): Timer => {
    const l = limits();
    if (typeof ms !== "number" || !Number.isFinite(ms) || ms < 0) throw new RangeError("the interval must be a number of milliseconds");
    if (repeat && ms < l.timerMinMs) throw new RangeError(`$.clock.every needs at least ${l.timerMinMs} ms`);
    if (count(o) >= l.timersPerSession) throw new RangeError(`a mod can have ${l.timersPerSession} timers per session`);

    let running = false;
    let handle: ReturnType<typeof setTimeout> | ReturnType<typeof setInterval>;
    const entry: Live = {
      mod: o.mod,
      session: o.session,
      stop: () => {
        (repeat ? clearInterval : clearTimeout)(handle as never);
        live.delete(entry);
      },
    };
    const tick = () => {
      if (!o.alive()) return entry.stop();
      if (!repeat) live.delete(entry);
      if (running) return;
      running = true;
      let result: void | Promise<void>;
      try {
        result = o.run(fn);
      } catch (e) {
        running = false;
        o.failed(e);
        return;
      }
      if (result && typeof (result as Promise<void>).then === "function") {
        (result as Promise<void>).then(
          () => void (running = false),
          (e) => {
            running = false;
            o.failed(e);
          },
        );
      } else {
        running = false;
      }
    };
    handle = repeat ? setInterval(tick, ms) : setTimeout(tick, ms);
    live.add(entry);
    return Object.freeze({ cancel: entry.stop });
  };

  return {
    after: (o, ms, fn) => start(o, ms, fn, false),
    every: (o, ms, fn) => start(o, ms, fn, true),
    stopMod(mod) {
      for (const t of [...live]) if (t.mod === mod) t.stop();
    },
    stopSession(session) {
      for (const t of [...live]) if (t.session === session) t.stop();
    },
    stopAll() {
      for (const t of [...live]) t.stop();
    },
  };
}
