/**
 * Append a structured finding to tests/beta-harness/findings/ — the durable record of what
 * Claude observed during a beta run. Stable repros graduate to C# E2E tests later.
 */

import { mkdirSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = resolve(fileURLToPath(import.meta.url), "..", "..");
const FINDINGS_DIR = resolve(HERE, "findings");

export type FindingResult = "pass" | "suspected-bug" | "inconclusive";

export interface FindingOptions {
  scenarioId: string;
  result: FindingResult;
  /** Numbered, copy-pasteable repro steps. */
  repro: string[];
  /** Log excerpts, screenshot paths, network response snippets — anything Claude observed. */
  evidence?: string[];
  /** Optional next probe hint. */
  nextProbe?: string;
}

/** Write a finding markdown file and return its absolute path. */
export function recordFinding(opts: FindingOptions): string {
  mkdirSync(FINDINGS_DIR, { recursive: true });
  const stamp = nowStamp();
  const fileName = `${stamp}-${opts.scenarioId}.md`;
  const path = resolve(FINDINGS_DIR, fileName);

  const body = renderFinding(opts, stamp);
  writeFileSync(path, body, "utf8");
  return path;
}

function renderFinding(opts: FindingOptions, stamp: string): string {
  const reproBlock = opts.repro.map((step, i) => `${i + 1}. ${step}`).join("\n");
  const evidence = opts.evidence?.length
    ? opts.evidence.map((line) => `- ${line}`).join("\n")
    : "_(none)_";
  const next = opts.nextProbe ?? "_(none)_";

  return [
    `# Finding: ${opts.scenarioId}`,
    "",
    `- **Recorded:** ${stamp}`,
    `- **Result:** ${opts.result}`,
    "",
    "## Repro",
    "",
    reproBlock,
    "",
    "## Evidence",
    "",
    evidence,
    "",
    "## Next probe",
    "",
    next,
    "",
  ].join("\n");
}

function nowStamp(): string {
  const d = new Date();
  const pad = (n: number, w = 2): string => String(n).padStart(w, "0");
  return [
    d.getUTCFullYear(),
    "-",
    pad(d.getUTCMonth() + 1),
    "-",
    pad(d.getUTCDate()),
    "-",
    pad(d.getUTCHours()),
    pad(d.getUTCMinutes()),
  ].join("");
}
