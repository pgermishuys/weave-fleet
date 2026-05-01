/**
 * Tail the fleet log without flooding context. Always grep — never read the whole file.
 */

import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = resolve(fileURLToPath(import.meta.url), "..", "..");
const DEFAULT_LOG = resolve(HERE, ".runtime", "fleet.log");

export interface TailOptions {
  /** Optional regex source (case-insensitive). Lines that don't match are skipped. */
  grep?: string;
  /** Max lines to return. */
  lines?: number;
  /** Override log path. */
  logPath?: string;
}

/**
 * Returns up to `lines` most-recent matching lines from fleet.log. Reads the whole file
 * synchronously — fine for ~MB logs in beta runs. The caller decides what to do with them.
 */
export function tailLog(opts: TailOptions = {}): string[] {
  const path = opts.logPath ?? DEFAULT_LOG;
  const lines = opts.lines ?? 50;
  const re = opts.grep ? new RegExp(opts.grep, "i") : null;

  let content: string;
  try {
    content = readFileSync(path, "utf8");
  } catch {
    return [];
  }

  const all = content.split(/\r?\n/);
  const matched = re ? all.filter((l) => re.test(l)) : all;
  return matched.slice(Math.max(0, matched.length - lines));
}
