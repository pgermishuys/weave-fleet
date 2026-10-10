import type { Json } from "./types";

/** Freezes `value` and everything inside it. Returns `value`. */
export function deepFreeze<T>(value: T): T {
  if (typeof value === "object" && value !== null && !Object.isFrozen(value)) {
    Object.freeze(value);
    for (const v of Object.values(value)) deepFreeze(v);
  }
  return value;
}

/** A frozen copy: what a hook sees as `e`, and what it passes to `next`. */
export function frozenCopy<T>(value: T): T {
  return deepFreeze(structuredClone(value));
}

const MAX_DEPTH = 100;

/** Checks that `value` is plain JSON (no undefined, functions, NaN, Date, Map, cycles…) and returns it as JSON text. */
export function toJsonText(value: unknown): string {
  assertJson(value, 0, new Set());
  return JSON.stringify(value);
}

function assertJson(v: unknown, depth: number, path: Set<object>): void {
  if (depth > MAX_DEPTH) throw new TypeError("value is nested too deeply to be JSON");
  if (v === null || typeof v === "string" || typeof v === "boolean") return;
  if (typeof v === "number") {
    if (!Number.isFinite(v)) throw new TypeError("value is not JSON: numbers must be finite");
    return;
  }
  if (typeof v !== "object") throw new TypeError(`value is not JSON: ${typeof v}`);
  if (path.has(v)) throw new TypeError("value is not JSON: it contains itself");
  path.add(v);
  if (Array.isArray(v)) {
    for (const item of v) assertJson(item, depth + 1, path);
  } else {
    const proto = Object.getPrototypeOf(v);
    if (proto !== Object.prototype && proto !== null) throw new TypeError("value is not JSON: only plain objects are allowed");
    for (const k of Object.keys(v)) assertJson((v as Record<string, unknown>)[k], depth + 1, path);
  }
  path.delete(v);
}

export const parseJson = (text: string): Json => JSON.parse(text) as Json;
