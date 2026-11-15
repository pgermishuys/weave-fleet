/**
 * What the quiet tool rows say about a call, worked out the same for every harness from the tool registry's category and
 * the call's own state: how long it took, how a shell call ended, and which rows a conversation collapses together.
 */
import { getTool, parseToolInput } from "@/lib/tools";
import { formatDuration } from "@/lib/format";

/** A call's run time as a row says it: "0.1 s", "3.4 s", "34 s", "2 min 05 s". Empty when nothing was timed. */
export function formatCallDuration(ms: number | undefined): string {
  if (ms === undefined || ms < 0) return "";
  return formatDuration(ms, { tenths: true });
}

/** The shell header: "bun test · exit 0 · 0.1 s", leaving out what the harness doesn't report. */
export function shellHeader(input: Record<string, unknown> | undefined, fallback: string, exit?: number, durationMs?: number): string {
  const command = parseToolInput(input).command?.split("\n")[0]?.trim() || fallback;
  const parts = [command];
  if (exit !== undefined) parts.push(`exit ${exit}`);
  const took = formatCallDuration(durationMs);
  if (took) parts.push(took);
  return parts.join(" · ");
}

/** A read: a file or a folder looked at, which the conversation dims and collapses with the reads beside it. */
export function isReadCall(kind: string | undefined): boolean {
  const tool = getTool(kind);
  return tool.category === "read";
}

export function isWriteCall(kind: string | undefined): boolean {
  return getTool(kind).fileWrite !== undefined;
}

export function isShellRow(kind: string | undefined): boolean {
  const tool = getTool(kind);
  return tool.category === "shell" && tool.name !== "execute";
}

/**
 * A read that collapses into a "Read N files" row: done, or still on its way (its path may not have arrived yet, so
 * alone it would read as a bare "Read"). A failed or stopped read keeps its own row: a failure is never folded away.
 */
export function isGroupableRead(tool: { kind?: string; status?: string; delegation?: unknown }): boolean {
  return !tool.delegation && isReadCall(tool.kind) && (tool.status === "Completed" || tool.status === "Running" || tool.status === "Pending");
}

export type ToolRowEntry<T> = { type: "tool"; tool: T } | { type: "reads"; tools: T[]; id: string };

/**
 * The rows a message draws: two or more reads in a row (done or still streaming) become one "Read N files" entry. A
 * failed read keeps its own row.
 */
export function groupReads<T extends { id: string; kind?: string; status?: string; delegation?: unknown }>(tools: readonly T[]): ToolRowEntry<T>[] {
  const entries: ToolRowEntry<T>[] = [];
  let run: T[] = [];
  const flush = (): void => {
    if (run.length >= 2) entries.push({ type: "reads", tools: run, id: `reads-${run[0]!.id}` });
    else for (const tool of run) entries.push({ type: "tool", tool });
    run = [];
  };
  for (const tool of tools) {
    if (isGroupableRead(tool)) {
      run.push(tool);
      continue;
    }
    flush();
    entries.push({ type: "tool", tool });
  }
  flush();
  return entries;
}
