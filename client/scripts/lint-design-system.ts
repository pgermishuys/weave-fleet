#!/usr/bin/env node
// Design lint, run as a ratchet. It counts three kinds of drift from the design system in every .vue file
// under src/ (src/components/ui/ is the design system itself, so it's skipped):
//
//   RAW_BUTTON        a raw <button> where the <Button> component would do
//   HARDCODED_RADIUS  a border-radius that isn't 0, 50% or a var(--…) token
//   HARDCODED_TIMING  a transition or animation duration that isn't a var(--transition…) token
//
// The counts are compared with design-lint-baseline.json. A count above its baseline fails (the output names
// the files that grew); a count below it passes and asks you to lower the baseline, so it only ever goes down.
//
//   node scripts/lint-design-system.ts            check against the baseline (part of `lint`)
//   node scripts/lint-design-system.ts --update   write the current counts as the new baseline
//   node scripts/lint-design-system.ts --list RAW_BUTTON   print every violation of one rule
import { readdirSync, readFileSync, writeFileSync } from "node:fs";
import { join, relative, sep } from "node:path";
import { pathToFileURL } from "node:url";

export const RULES = ["RAW_BUTTON", "HARDCODED_RADIUS", "HARDCODED_TIMING"] as const;
export type Rule = (typeof RULES)[number];

export interface Violation {
  rule: Rule;
  file: string;
  line: number;
  detail: string;
}

export interface Baseline {
  rules: Record<Rule, { total: number; files: Record<string, number> }>;
}

// Run from client/ (npm and bun scripts do).
const clientRoot = process.cwd();
const baselinePath = join(clientRoot, "scripts", "design-lint-baseline.json");

function findVueFiles(dir: string, skip: string[]): string[] {
  const files: string[] = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === "__tests__" || skip.includes(full)) continue;
      files.push(...findVueFiles(full, skip));
    } else if (entry.name.endsWith(".vue")) {
      files.push(full);
    }
  }
  return files.sort();
}

interface Block {
  text: string;
  /** 1-based line number of the block's first line in the file. */
  firstLine: number;
}

// An SFC's top-level <template> and <style> blocks. Nested <template v-if> / <template #slot> tags are counted,
// so the template block only ends at the </template> that closes the outer one.
export function extractBlocks(content: string): { template: Block[]; style: Block[] } {
  const lines = content.split("\n");
  const template: Block[] = [];
  const style: Block[] = [];
  let open: { kind: "template" | "style"; start: number } | null = null;
  let depth = 0;
  lines.forEach((line, i) => {
    if (!open) {
      if (/^<template[\s>]/.test(line)) {
        open = { kind: "template", start: i + 1 };
        depth = 1;
      } else if (/^<style[\s>]/.test(line)) {
        open = { kind: "style", start: i + 1 };
      }
      return;
    }
    if (open.kind === "template") {
      depth += (line.match(/<template(?=[\s>]|$)/g) ?? []).length;
      depth -= (line.match(/<\/template>/g) ?? []).length;
      if (depth <= 0) {
        template.push({ text: lines.slice(open.start, i).join("\n"), firstLine: open.start + 1 });
        open = null;
      }
    } else if (/^<\/style>/.test(line)) {
      style.push({ text: lines.slice(open.start, i).join("\n"), firstLine: open.start + 1 });
      open = null;
    }
  });
  return { template, style };
}

function lineAt(block: Block, index: number): number {
  let line = block.firstLine;
  for (let i = 0; i < index; i++) if (block.text.charCodeAt(i) === 10) line++;
  return line;
}

// Opening <button> tags, including ones whose attributes run over several lines.
export function checkRawButtons(file: string, template: Block[]): Violation[] {
  const out: Violation[] = [];
  for (const block of template) {
    for (const m of block.text.matchAll(/<button(?=[\s>/])[^>]*>?/g)) {
      out.push({ rule: "RAW_BUTTON", file, line: lineAt(block, m.index), detail: "raw <button>; use <Button>" });
    }
  }
  return out;
}

// Declarations of the matching properties, several per line too (`.pill { border-radius: 999px; }`). The value
// runs to the next ; or brace, so it's never cut at a later colon.
function declarations(block: Block, property: RegExp): { line: number; value: string }[] {
  const out: { line: number; value: string }[] = [];
  block.text.split("\n").forEach((raw, i) => {
    for (const m of raw.matchAll(/(?<![\w-])([a-z-]+)\s*:\s*([^;{}]+)/g)) {
      if (property.test(m[1])) out.push({ line: block.firstLine + i, value: m[2].trim() });
    }
  });
  return out;
}

export function checkBorderRadius(file: string, style: Block[]): Violation[] {
  const out: Violation[] = [];
  for (const block of style) {
    for (const { line, value } of declarations(block, /^border(-[a-z]+)*-radius$/)) {
      if (/^0(px)?(\s|$)/.test(value) || /^50%(\s|$)/.test(value) || value.startsWith("var(") || value === "inherit") continue;
      out.push({ rule: "HARDCODED_RADIUS", file, line, detail: `border-radius: ${value}` });
    }
  }
  return out;
}

export function checkTransitionTimings(file: string, style: Block[]): Violation[] {
  const out: Violation[] = [];
  for (const block of style) {
    // Durations inside @keyframes are percentages, not timings; skip those blocks.
    const text = block.text.replace(/@keyframes[^{]*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}/g, (m) => m.replace(/[^\n]/g, " "));
    const masked = { text, firstLine: block.firstLine };
    for (const { line, value } of declarations(masked, /^(transition|animation)(-[a-z]+)?$/)) {
      if (/var\(--transition/.test(value)) continue;
      const time = value.match(/(?<![\w.-])\d*\.?\d+m?s\b/);
      if (time) out.push({ rule: "HARDCODED_TIMING", file, line, detail: `hardcoded duration ${time[0]}` });
    }
  }
  return out;
}

export function lintFile(file: string, content: string): Violation[] {
  const { template, style } = extractBlocks(content);
  return [...checkRawButtons(file, template), ...checkBorderRadius(file, style), ...checkTransitionTimings(file, style)];
}

export function lintClient(root: string = clientRoot): Violation[] {
  const src = join(root, "src");
  return findVueFiles(src, [join(src, "components", "ui")]).flatMap((full) =>
    lintFile(relative(root, full).split(sep).join("/"), readFileSync(full, "utf8")),
  );
}

export function countsOf(violations: Violation[]): Baseline {
  const rules = Object.fromEntries(RULES.map((r) => [r, { total: 0, files: {} as Record<string, number> }])) as Baseline["rules"];
  for (const v of violations) {
    rules[v.rule].total++;
    rules[v.rule].files[v.file] = (rules[v.rule].files[v.file] ?? 0) + 1;
  }
  return { rules };
}

export interface RatchetResult {
  ok: boolean;
  messages: string[];
}

// Fails when a rule's total rises above the baseline. A fall passes, with a nudge to lower the baseline.
export function compare(current: Baseline, baseline: Baseline): RatchetResult {
  const messages: string[] = [];
  let ok = true;
  for (const rule of RULES) {
    const now = current.rules[rule];
    const was = baseline.rules[rule] ?? { total: 0, files: {} };
    if (now.total > was.total) {
      ok = false;
      messages.push(`✗ ${rule}: ${now.total}, baseline ${was.total} (+${now.total - was.total}). Files that grew:`);
      for (const [file, n] of Object.entries(now.files)) {
        const before = was.files[file] ?? 0;
        if (n > before) messages.push(`    ${file}: ${before} → ${n}`);
      }
    } else if (now.total < was.total) {
      messages.push(
        `↓ ${rule}: ${now.total}, baseline ${was.total} (−${was.total - now.total}). ` +
          "Lower the baseline so it stays down: node scripts/lint-design-system.ts --update",
      );
    } else {
      messages.push(`✓ ${rule}: ${now.total} (baseline ${was.total})`);
    }
  }
  return { ok, messages };
}

function main(argv: string[]): number {
  const violations = lintClient();
  const current = countsOf(violations);

  const listAt = argv.indexOf("--list");
  if (listAt >= 0) {
    const rule = argv[listAt + 1];
    for (const v of violations) if (!rule || v.rule === rule) console.log(`[${v.rule}] ${v.file}:${v.line}: ${v.detail}`);
    return 0;
  }

  if (argv.includes("--update")) {
    writeFileSync(baselinePath, JSON.stringify(current, null, 2) + "\n");
    console.log(`Design lint baseline written: ${RULES.map((r) => `${r} ${current.rules[r].total}`).join(", ")}`);
    return 0;
  }

  const baseline = JSON.parse(readFileSync(baselinePath, "utf8")) as Baseline;
  const result = compare(current, baseline);
  console.log("Design lint (ratchet against scripts/design-lint-baseline.json)");
  for (const m of result.messages) console.log(m);
  if (!result.ok) {
    console.log("\nUse the design system instead (see DESIGN_SYSTEM.md); `--list <RULE>` prints every violation.");
  }
  return result.ok ? 0 : 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exit(main(process.argv.slice(2)));
}
