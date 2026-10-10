/**
 * A hook's own-time budget: wall time that runs only while nothing is awaited inside `next` or a `$` call. Overlapping
 * waits are counted, so the clock stops at the first and restarts when the last ends.
 */
export interface Budget {
  readonly ms: number;
  readonly remainingMs: number;
  pause(): void;
  resume(): void;
  /** Stops the clock for good; nothing fires afterwards. */
  finish(): void;
}

export function createBudget(ms: number, onExpire: () => void, now: () => number = Date.now): Budget {
  let used = 0;
  let since = now();
  let waits = 0;
  let done = false;
  let timer: ReturnType<typeof setTimeout> | undefined;

  const arm = () => {
    timer = setTimeout(() => {
      timer = undefined;
      if (!done) {
        done = true;
        used = ms;
        onExpire();
      }
    }, Math.max(0, ms - used));
  };
  arm();

  return {
    ms,
    get remainingMs() {
      const running = waits === 0 && !done ? now() - since : 0;
      return Math.max(0, ms - used - running);
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
    finish() {
      done = true;
      clearTimeout(timer);
      timer = undefined;
    },
  };
}
