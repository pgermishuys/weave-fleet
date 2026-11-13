/**
 * Invented, deterministic content for seeding: project names, session titles, prompts, tool calls and
 * markdown answers sized like a real coding session. Nothing here comes from real data.
 */

export function rng(seed: number): () => number {
  let s = seed >>> 0;
  return () => {
    s = (s + 0x6d2b79f5) >>> 0;
    let t = s;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

export const PROJECTS = ["lantern-api", "orchard-web", "tidepool-cli", "quarry-infra", "maple-docs", "beacon-mobile"];

const VERBS = ["Fix", "Add", "Refactor", "Speed up", "Document", "Investigate", "Remove", "Rename", "Test", "Migrate"];
const THINGS = [
  "the invoice export", "retry backoff in the sync worker", "the settings page layout", "flaky login test",
  "dark mode colours", "the CSV importer", "pagination on the orders list", "the webhook signature check",
  "cache invalidation for avatars", "the onboarding checklist", "memory use in the report builder",
  "the search ranking", "timezone handling in reminders", "the release notes script", "keyboard shortcuts",
  "the audit log table", "image resizing", "the rate limiter", "error messages on the payment form", "the plugin loader",
];
const FILES = [
  "src/orders/export.ts", "src/sync/worker.ts", "src/settings/SettingsPage.vue", "tests/login.spec.ts",
  "src/theme/colors.css", "src/import/csv.ts", "src/orders/OrdersList.vue", "src/webhooks/verify.ts",
  "src/avatars/cache.ts", "src/onboarding/checklist.ts", "src/reports/builder.ts", "src/search/rank.ts",
  "src/reminders/schedule.ts", "scripts/release-notes.ts", "src/shortcuts/keymap.ts", "src/audit/AuditTable.vue",
];

export function pick<T>(r: () => number, xs: readonly T[]): T {
  return xs[Math.floor(r() * xs.length)]!;
}

export function title(r: () => number): string {
  return `${pick(r, VERBS)} ${pick(r, THINGS)}`;
}

export function prompt(r: () => number, turn: number): string {
  const asks = [
    `Can you look at ${pick(r, FILES)} and tell me why ${pick(r, THINGS)} breaks when the list is empty?`,
    `Run the tests for ${pick(r, FILES)} and fix whatever fails.`,
    `Make ${pick(r, THINGS)} handle the edge case we talked about, and add a test for it.`,
    `That looks right. Now update the docs and the changelog.`,
    `Please refactor ${pick(r, FILES)} so the parsing and the I/O are separate functions.`,
    `Why is ${pick(r, THINGS)} slow on large inputs? Measure before changing anything.`,
  ];
  return `${pick(r, asks)} (step ${turn + 1})`;
}

function codeLines(r: () => number, n: number): string {
  const lines: string[] = [];
  for (let i = 0; i < n; i++) {
    const k = Math.floor(r() * 6);
    const name = pick(r, ["items", "order", "row", "result", "config", "entry", "batch"]);
    lines.push(
      k === 0 ? `export function handle${i}(${name}: Input): Output {`
        : k === 1 ? `  const ${name}${i} = await load(${name}, { retries: ${i % 5}, timeoutMs: ${1000 + i * 10} });`
        : k === 2 ? `  if (!${name}) throw new Error("missing ${name} at line ${i}");`
        : k === 3 ? `  return ${name}.map((x) => ({ ...x, total: x.price * x.quantity }));`
        : k === 4 ? `}`
        : `  // ${pick(r, THINGS)}: keep this in step with the schema`,
    );
  }
  return lines.join("\n");
}

export interface ToolCall {
  tool: string;
  input: Record<string, unknown>;
  output: string;
  title: string;
  metadata?: Record<string, unknown>;
}

export function toolCalls(r: () => number, count: number): ToolCall[] {
  const calls: ToolCall[] = [];
  for (let i = 0; i < count; i++) {
    const file = pick(r, FILES);
    const kind = Math.floor(r() * 5);
    if (kind === 0) {
      calls.push({ tool: "read", input: { filePath: file }, title: file, output: codeLines(r, 40 + Math.floor(r() * 80)) });
    } else if (kind === 1) {
      const passed = 20 + Math.floor(r() * 200);
      const out = Array.from({ length: 25 + Math.floor(r() * 30) }, (_, j) => `  ✓ ${pick(r, THINGS)} case ${j} (${Math.floor(r() * 40)} ms)`);
      calls.push({
        tool: "bash", input: { command: "bun run test -- " + file, description: "Run the tests" }, title: "Run the tests",
        output: `${out.join("\n")}\n\n Test Files  ${Math.ceil(passed / 10)} passed\n      Tests  ${passed} passed\n`,
      });
    } else if (kind === 2) {
      const before = codeLines(r, 6);
      const after = codeLines(r, 8);
      const diff = `--- a/${file}\n+++ b/${file}\n@@ -10,6 +10,8 @@\n${before.split("\n").map((l) => "-" + l).join("\n")}\n${after.split("\n").map((l) => "+" + l).join("\n")}\n`;
      calls.push({ tool: "edit", input: { filePath: file, oldString: before, newString: after }, title: file, output: "Edit applied successfully.", metadata: { diff } });
    } else if (kind === 3) {
      const hits = Array.from({ length: 8 + Math.floor(r() * 20) }, () => `${pick(r, FILES)}:${1 + Math.floor(r() * 300)}: ${codeLines(r, 1)}`);
      calls.push({ tool: "grep", input: { pattern: pick(r, ["retry", "export", "cache", "timeout"]), path: "src" }, title: "grep", output: hits.join("\n") });
    } else {
      calls.push({ tool: "glob", input: { pattern: "src/**/*.ts" }, title: "src/**/*.ts", output: FILES.join("\n") });
    }
  }
  return calls;
}

export function answer(r: () => number): string {
  const bullets = Array.from({ length: 3 + Math.floor(r() * 4) }, () => `- ${pick(r, VERBS)} ${pick(r, THINGS)} in \`${pick(r, FILES)}\``);
  const rows = Array.from({ length: 4 }, (_, i) => `| ${pick(r, FILES)} | ${10 + i * 7} | ${Math.floor(r() * 100)}% |`);
  return [
    `## What I changed`,
    ``,
    `The problem was in \`${pick(r, FILES)}\`: ${pick(r, THINGS)} assumed the input was never empty, so the first call after a restart threw before the retry could start.`,
    ``,
    ...bullets,
    ``,
    "```ts",
    codeLines(r, 12 + Math.floor(r() * 18)),
    "```",
    ``,
    `| File | Lines | Coverage |`,
    `|------|-------|----------|`,
    ...rows,
    ``,
    `All tests pass now. Next I'd look at **${pick(r, THINGS)}**, which has the same pattern.`,
  ].join("\n");
}
