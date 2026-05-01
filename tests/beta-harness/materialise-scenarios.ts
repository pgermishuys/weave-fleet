/**
 * Read every scenarios/*.md playbook, extract the embedded ```json``` block, and
 * write it to .runtime/scenarios/<id>.json. Run before start-fleet so the harness
 * sees fresh scenario JSON.
 *
 * Single source of truth: editing the playbook markdown updates the harness scenario.
 */

import { mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = resolve(fileURLToPath(import.meta.url), "..");
const SCENARIOS_SRC = resolve(HERE, "scenarios");
const SCENARIOS_OUT = resolve(HERE, ".runtime", "scenarios");

interface PlaybookFrontmatter {
  id: string;
}

const FRONTMATTER_RE = /^---\r?\n([\s\S]*?)\r?\n---/;
const JSON_BLOCK_RE = /```json\r?\n([\s\S]*?)\r?\n```/;

function parseFrontmatter(source: string): PlaybookFrontmatter {
  const match = source.match(FRONTMATTER_RE);
  if (!match) throw new Error("missing frontmatter");
  const body = match[1] ?? "";
  const fields: Record<string, string> = {};
  for (const line of body.split(/\r?\n/)) {
    const m = line.match(/^([a-zA-Z_][\w-]*):\s*(.*)$/);
    if (m && m[1]) fields[m[1]] = m[2]?.trim() ?? "";
  }
  if (!fields["id"]) throw new Error("frontmatter missing 'id'");
  return { id: fields["id"] };
}

function extractJsonBlock(source: string): unknown {
  const match = source.match(JSON_BLOCK_RE);
  if (!match) throw new Error("missing ```json``` block");
  return JSON.parse(match[1] ?? "{}");
}

function main(): void {
  mkdirSync(SCENARIOS_OUT, { recursive: true });
  const entries = readdirSync(SCENARIOS_SRC, { withFileTypes: true })
    .filter((e) => e.isFile() && e.name.endsWith(".md"));

  if (entries.length === 0) {
    process.stdout.write(`no playbooks found in ${SCENARIOS_SRC}\n`);
    return;
  }

  let written = 0;
  let skipped = 0;
  for (const entry of entries) {
    const path = resolve(SCENARIOS_SRC, entry.name);
    const source = readFileSync(path, "utf8");
    try {
      const fm = parseFrontmatter(source);
      const json = extractJsonBlock(source);
      const out = resolve(SCENARIOS_OUT, `${fm.id}.json`);
      writeFileSync(out, `${JSON.stringify(json, null, 2)}\n`, "utf8");
      process.stdout.write(`materialised ${entry.name} -> ${fm.id}.json\n`);
      written += 1;
    } catch (err) {
      process.stderr.write(`skipped ${entry.name}: ${(err as Error).message}\n`);
      skipped += 1;
    }
  }

  process.stdout.write(`done. wrote ${written}, skipped ${skipped}.\n`);
}

main();
