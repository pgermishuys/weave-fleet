/**
 * A hook's own-time budget.
 *
 * Own time is wall time from the call until it settles, less two things: the time it waits inside `next` or a `$`
 * call (overlapping waits count once), and the time other mod code the host runs takes: another hook's or `.catch`
 * handler's synchronous part, a timer, a callback (see `Meter.run`). So a busy hook in another session doesn't time
 * out an innocent one. Bun can't measure CPU time, and code after another hook's `await` runs where the host can't
 * see it, so that code's time still counts against hooks waiting beside it.
 */
export interface Budget {
  readonly ms: number;
  readonly remainingMs: number;
  pause(): void;
  resume(): void;
  /** Stops the clock for good; nothing fires afterwards. */
  finish(): void;
}

/** The running budgets, and the mod code the host runs: the time one owner's code takes isn't charged to another's budget. */
export interface Meter {
  /** A budget for code of `owner` (a mod in a session): `onExpire` runs once its own time passes `ms`. */
  budget(ms: number, onExpire: () => void, owner: string): Budget;
  /** Runs `fn`, synchronous code of `owner`, and gives the time it took back to every other owner's running budget. */
  run<T>(owner: string, fn: () => T): T;
}

/** The owner key for code of `mod` running for `session`. */
export const ownerKey = (mod: string, session: string) => `${mod}\u0000${session}`;

interface Live {
  owner: string;
  /** Takes back the part of [start, now] the budget was running. */
  credit(start: number): void;
}

export function createMeter(now: () => number = Date.now): Meter {
  const live = new Set<Live>();
  const stack: { owner: string; start: number }[] = [];

  const giveBack = (owner: string, start: number) => {
    for (const b of live) if (b.owner !== owner) b.credit(start);
  };

  return {
    run(owner, fn) {
      const outer = stack.at(-1);
      if (outer) giveBack(outer.owner, outer.start);
      const segment = { owner, start: now() };
      stack.push(segment);
      try {
        return fn();
      } finally {
        stack.pop();
        giveBack(owner, segment.start);
        if (outer) outer.start = now();
      }
    },

    budget(ms, onExpire, owner) {
      let used = 0;
      let since = now();
      let waits = 0;
      let done = false;
      let timer: ReturnType<typeof setTimeout> | undefined;
      const running = () => (waits === 0 && !done ? now() - since : 0);

      const stop = () => {
        done = true;
        clearTimeout(timer);
        timer = undefined;
        live.delete(entry);
      };
      const arm = () => {
        timer = setTimeout(() => {
          timer = undefined;
          if (done) return;
          // Time given back while the timer waited moved `since` on: the budget may have time left.
          if (ms - used - running() > 1) return arm();
          stop();
          used = ms;
          onExpire();
        }, Math.max(0, ms - used - running()));
      };
      const entry: Live = {
        owner,
        credit(start) {
          if (waits > 0 || done) return;
          const t = now();
          since += Math.max(0, t - Math.max(start, since));
        },
      };
      live.add(entry);
      arm();

      return {
        ms,
        get remainingMs() {
          return Math.max(0, ms - used - running());
        },
        pause() {
          if (done) return;
          if (waits++ === 0) {
            used += now() - since;
            clearTimeout(timer);
            timer = undefined;
          }
        },
        resume() {
          if (done) return;
          if (waits > 0 && --waits === 0) {
            since = now();
            arm();
          }
        },
        finish: stop,
      };
    },
  };
}
