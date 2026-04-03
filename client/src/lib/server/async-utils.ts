/**
 * Shared async utilities for the server layer.
 */

/**
 * Race a promise against a timeout. Rejects with a TimeoutError if the
 * promise does not settle within `ms` milliseconds. The timer is always
 * cleaned up — no leaked timers on success.
 */
export class TimeoutError extends Error {
  constructor(label: string, ms: number) {
    super(`${label}: timed out after ${ms}ms`);
    this.name = "TimeoutError";
  }
}

export function withTimeout<T>(promise: Promise<T>, ms: number, label: string): Promise<T> {
  let timer: ReturnType<typeof setTimeout>;
  const timeoutPromise = new Promise<never>((_resolve, reject) => {
    timer = setTimeout(() => {
      reject(new TimeoutError(label, ms));
    }, ms);
    // Don't prevent Node.js from exiting
    if (timer.unref) {
      timer.unref();
    }
  });

  return Promise.race([promise, timeoutPromise]).finally(() => {
    clearTimeout(timer);
  });
}

const DEFAULT_SDK_CALL_TIMEOUT_MS = 10_000;

/**
 * Returns the configured timeout (ms) for SDK HTTP calls
 * (`session.get`, `session.status`, `session.list`, etc.).
 *
 * Override with the `WEAVE_SDK_CALL_TIMEOUT_MS` environment variable.
 * Must be a positive integer (milliseconds). Invalid values fall back to
 * the default of 10 000 ms.
 */
export function getSDKCallTimeoutMs(): number {
  const envVal = parseInt(process.env.WEAVE_SDK_CALL_TIMEOUT_MS ?? "", 10);
  return Number.isFinite(envVal) && envVal > 0 ? envVal : DEFAULT_SDK_CALL_TIMEOUT_MS;
}
