import { AsyncLocalStorage } from "node:async_hooks";
import type { ModContext, Runtime } from "./types";

/** Which mod (and session) the running code belongs to. */
export const modContext = new AsyncLocalStorage<ModContext>();

const LINE_CHARS = 8192;

/** Cuts a log line to 8 KiB. */
export const capLine = (text: string) => (text.length > LINE_CHARS ? text.slice(0, LINE_CHARS) : text);

/** The context for code of `modId` running for `sessionId`: its logs go to Fleet as `log` notifications. */
export function contextFor(rt: Runtime, modId: string, sessionId?: string, event?: ModContext["event"]): ModContext {
  return {
    modId,
    sessionId,
    event,
    emit: (level, text) => {
      const params: Record<string, unknown> = { mod: modId, level, text: capLine(text) };
      if (sessionId !== undefined) params.sessionId = sessionId;
      rt.peer.notify("log", params);
    },
  };
}

/** Runs `fn` as mod code. */
export function inMod<T>(ctx: ModContext, fn: () => T): T {
  return modContext.run(ctx, fn);
}

const LEVELS = { log: "info", info: "info", debug: "info", warn: "warn", error: "error" } as const;
let installed = false;

/** Makes `console.*` inside mod code a log line of that mod. Elsewhere `console` is left as it is. Safe to call twice. */
export function installConsole(): void {
  if (installed) return;
  installed = true;
  for (const method of Object.keys(LEVELS) as (keyof typeof LEVELS)[]) {
    const original = console[method].bind(console);
    console[method] = (...args: unknown[]) => {
      const ctx = modContext.getStore();
      if (!ctx) return original(...args);
      ctx.emit(LEVELS[method], args.map((a) => (typeof a === "string" ? a : Bun.inspect(a))).join(" "));
    };
  }
}
