import type { Limits } from "fleet-mods";

/** The contract's limits (docs/mods/api.md, "Limits"). The host takes a copy so tests can shrink the timings. */
export type HostLimits = { -readonly [K in keyof Limits]: number } & {
  /** How long the host waits for Fleet to answer one of its requests (`store.get`, `session.get`…). */
  fleetRequestMs: number;
  /** One protocol line, either direction. */
  lineBytes: number;
  /** How long `shutdown` waits for running work before the host exits. */
  shutdownMs: number;
  /** Nested scopes in a hooks module. */
  moduleScopes: number;
};

export const LIMITS: Readonly<HostLimits> = Object.freeze({
  hookMs: 10_000,
  catchMs: 1_000,
  dispatchMs: 15_000,
  strikes: 3,
  treeTextChars: 100_000,
  treeNodes: 2_000,
  treeDepth: 32,
  treeBytes: 262_144,
  storeBytes: 4_194_304,
  stateBytes: 1_048_576,
  invalidatePerSecond: 10,
  timerMinMs: 100,
  timersPerSession: 20,
  moduleBytes: 524_288,
  toastChars: 500,
  fleetRequestMs: 10_000,
  lineBytes: 8 * 1024 * 1024,
  shutdownMs: 2_000,
  moduleScopes: 2_000,
});
