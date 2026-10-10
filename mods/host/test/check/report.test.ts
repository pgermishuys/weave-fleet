import { describe, expect, test } from "bun:test";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { checkMod, formatReport } from "../../src/check";

const fixtures = join(import.meta.dir, "..", "fixtures");
const sha = (bytes: Uint8Array) => new Bun.CryptoHasher("sha256").update(bytes).digest("hex");

describe("test-chips, the contract's worked example", () => {
  const root = join(fixtures, "mods", "test-chips");

  test("the full report", async () => {
    const r = await checkMod(root);
    expect(r.report).toEqual({
      ok: true,
      name: "test-chips",
      version: "0.1.0",
      description: "Draws test runs as passed, failed and skipped counts",
      lines: 47,
      sha256: sha(new Uint8Array(await readFile(join(root, "mod.ts")))),
      hooks: [{ event: "ui.render", matcher: { component: ["ToolUse", "ToolResult"], props: { tool: ["bash", "shell"] } } }],
      calls: ["ui.resolve"],
      state: [],
      pages: [],
      errors: [],
      warnings: [],
    });
    expect(r.manifest).toEqual({
      name: "test-chips",
      version: "0.1.0",
      description: "Draws test runs as passed, failed and skipped counts",
      hooks: "./mod.ts",
    });
    expect(r.modulePath).toBe(join(root, "mod.ts"));
    expect(r.js).toHaveLength((await readFile(join(root, "mod.ts"), "utf8")).length);
  });

  test("the text form", async () => {
    expect(formatReport((await checkMod(root)).report)).toBe(
      [
        "test-chips 0.1.0 · 47 lines",
        'hooks:  ui.render ["ToolUse","ToolResult"] { props: { tool: ["bash","shell"] } }',
        "calls:  ui.resolve",
        "state:  (none)",
        "pages:  (none)",
      ].join("\n"),
    );
  });
});

describe("kitchen sink", () => {
  const root = join(fixtures, "check", "kitchen-sink");

  test("every field of the report", async () => {
    const r = await checkMod(root);
    const source = await readFile(join(root, "mod.js"));
    expect(r.report).toEqual({
      ok: true,
      name: "kitchen-sink",
      version: "1.2.3",
      description: "Uses every allowed $ call, some state keys and a page",
      lines: source.toString().trimEnd().split("\n").length,
      sha256: sha(new Uint8Array(source)),
      hooks: [
        { event: "session.start" },
        { event: "turn.complete" },
        { event: "ui.render", matcher: { component: { $regex: "^Tool", flags: "i" }, props: { n: [1, -2.5], ok: true, none: null } } },
        { event: "ui.press" },
        { event: "ui.input" },
        { event: "ui.select" },
      ],
      calls: [
        "clock.after", "clock.every", "clock.now", "mod.name", "mod.version", "session.cwd", "session.harness", "session.id",
        "session.surfaces", "session.title", "state.get", "state.set", "store.delete", "store.get", "store.keys", "store.set",
        "ui.close", "ui.invalidate", "ui.log", "ui.open", "ui.resolve", "ui.toast",
      ],
      state: ["count", "seen"],
      pages: ["pages/demo.html"],
      errors: [],
      warnings: [],
    });
    expect(r.js).toBe(source.toString());
  });

  test("the text form", async () => {
    const text = formatReport((await checkMod(root)).report);
    expect(text.split("\n")).toEqual([
      expect.stringMatching(/^kitchen-sink 1\.2\.3 · \d+ lines$/),
      "hooks:  session.start",
      "        turn.complete",
      '        ui.render /^Tool/i { props: { n: [1,-2.5], ok: true, none: null } }',
      "        ui.press",
      "        ui.input",
      "        ui.select",
      expect.stringMatching(/^calls:  clock\.after, clock\.every, /),
      "state:  count, seen",
      "pages:  pages/demo.html",
    ]);
  });
});

describe("formatReport", () => {
  test("lists errors and warnings with position, code and message", () => {
    const text = formatReport({
      ok: false, name: "m", version: "1", description: "d", lines: 1, sha256: "", hooks: [], calls: [], state: [], pages: [],
      errors: [{ line: 12, column: 5, code: "global", message: "fetch isn't available to mods" }, { code: "manifest", message: "no" }],
      warnings: [{ line: 3, column: 1, code: "page-missing", message: "x" }],
    });
    expect(text).toBe(
      [
        "m 1 · 1 line",
        "hooks:  (none)",
        "calls:  (none)",
        "state:  (none)",
        "pages:  (none)",
        "error   12:5  global  fetch isn't available to mods",
        "error   -  manifest  no",
        "warning 3:1  page-missing  x",
      ].join("\n"),
    );
  });

  test("prints non-render matchers whole, and quotes odd keys", () => {
    const text = formatReport({
      ok: true, name: "m", version: "1", description: "d", lines: 2, sha256: "", calls: [], state: [], pages: [], errors: [], warnings: [],
      hooks: [{ event: "ui.press", matcher: { id: "a", "x-y": { $regex: "q", flags: "" }, empty: {} } }, { event: "ui.render", matcher: { component: "Box" } }],
    });
    expect(text.split("\n").slice(1, 3)).toEqual(['hooks:  ui.press { id: "a", "x-y": /q/, empty: {} }', '        ui.render "Box"']);
  });
});
