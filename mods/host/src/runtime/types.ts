import type { EventName, Json, Surface } from "fleet-mods/protocol-shared";
import type { HookFailureReport } from "fleet-mods/protocol";
import type { HostLimits } from "../limits";
import type { Clock } from "./clock";
import type { Handles } from "./handles";
import type { Invalidator } from "./invalidate";
import type { StateStore } from "./state";

export type ModId = string;
export type { EventName, Json, Surface, HookFailureReport };

/** What the host needs from the connection to Fleet; `RpcPeer` satisfies it. */
export interface Peer {
  handle(method: string, handler: (params: any) => unknown): void;
  request(method: string, params: unknown, options?: { timeoutMs?: number }): Promise<unknown>;
  notify(method: string, params: unknown): void;
}

export const EVENTS: readonly EventName[] = ["session.start", "turn.complete", "ui.render", "ui.press", "ui.input", "ui.select"];
/** Events whose hooks only watch: every hook runs and the return value is ignored. */
export const WATCH_ONLY: ReadonlySet<EventName> = new Set<EventName>(["session.start", "turn.complete"]);
export const CONTROL_EVENTS: ReadonlySet<EventName> = new Set<EventName>(["ui.press", "ui.input", "ui.select"]);

export type HookFn = ($: any, e: any, next: any) => unknown;

/** One `on(...)` registration. */
export interface HookReg {
  event: EventName;
  matcher?: Record<string, unknown>;
  hook: HookFn;
  catchHandler?: HookFn;
}

/** A loaded module. A reload makes a new one; `dead` marks the old. */
export interface LoadedMod {
  id: ModId;
  name: string;
  /** Fleet's version number, or "draft". */
  version: number | "draft";
  /** Set for drafts: the only session this module serves. */
  sessionId?: string;
  root: string;
  hooks: HookReg[];
  dead: boolean;
  /** Sessions this module has started (or is starting): the memoised session.start run. */
  starts: Map<string, Promise<HookFailureReport[]>>;
  /** Sessions the module this one replaced had started: their session.start says "reload". */
  prevStarted: Set<string>;
}

/** Where a piece of mod code is running, for `console` and for `$`. */
export interface ModContext {
  modId: ModId;
  sessionId?: string;
  event?: EventName;
  emit: (level: "info" | "warn" | "error", text: string) => void;
}

/** A running hook, callback or timer that can be aborted by forget or unload. */
export interface ActiveCall {
  modId: ModId;
  sessionId: string;
  abort: () => void;
}

export interface Runtime {
  peer: Peer;
  limits: HostLimits;
  log: (message: string) => void;
  now: () => number;
  mods: Map<ModId, LoadedMod>;
  strikes: Map<ModId, number>;
  state: StateStore;
  invalidator: Invalidator;
  clock: Clock;
  handles: Handles;
  calls: Set<ActiveCall>;
  /** Dispatches running now. */
  inflight: Set<Promise<unknown>>;
  /** Work that follows an answer (reload starts). */
  background: Set<Promise<unknown>>;
  /** A failure outside a dispatch (a timer, a callback): notify Fleet, count a strike, unload at the limit. */
  fail(modId: ModId, sessionId: string, event: EventName, kind: "throw" | "timeout", message: string): void;
  /** Stops and forgets a module. */
  unloadMod(id: ModId): void;
}
