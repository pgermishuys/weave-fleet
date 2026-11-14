import { describe, expect, it } from "vitest";
import { compare, countsOf, lintFile, type Baseline } from "../lint-design-system";

const sfc = `<script setup lang="ts">
const a = 1
</script>

<template>
  <div>
    <template v-if="a">
      <span>nested template</span>
</template>
    <button
      type="button"
      class="row"
    >
      Multi-line
    </button>
    <button class="one-line">One line</button>
    <button v-bind="$attrs">Home-made switch</button>
    <Button>Design system</Button>
  </div>
</template>

<style scoped>
.row {
  border-radius: 6px;
  border-top-left-radius: 0;
  transition: opacity 120ms ease;
}
.pill { border-radius: 999px; }
.round { border-radius: 50%; }
.token { border-radius: var(--radius-btn); transition: color var(--transition); }
@keyframes pulse {
  0% { opacity: 0; }
  100% { opacity: 1; }
}
.spin { animation: spin 1s linear infinite; }
</style>
`;

describe("design lint rules", () => {
  const found = lintFile("src/components/Example.vue", sfc);
  const of = (rule: string) => found.filter((v) => v.rule === rule).map((v) => v.line);

  it("finds raw buttons whose tag runs over several lines, after a nested </template> in column 0", () => {
    expect(of("RAW_BUTTON")).toEqual([10, 16, 17]);
  });

  it("reads a declaration's value past the colon (border-radius: 6px was never seen)", () => {
    expect(of("HARDCODED_RADIUS")).toEqual([24, 28]);
  });

  it("finds hardcoded durations outside @keyframes", () => {
    expect(of("HARDCODED_TIMING")).toEqual([26, 35]);
  });
});

describe("design lint ratchet", () => {
  const baseline: Baseline = countsOf([
    { rule: "RAW_BUTTON", file: "a.vue", line: 1, detail: "" },
    { rule: "RAW_BUTTON", file: "b.vue", line: 1, detail: "" },
    { rule: "HARDCODED_RADIUS", file: "a.vue", line: 2, detail: "" },
  ]);

  it("passes when nothing changed", () => {
    expect(compare(baseline, baseline).ok).toBe(true);
  });

  it("fails when a count rises, and names the file that grew", () => {
    const now = countsOf([
      { rule: "RAW_BUTTON", file: "a.vue", line: 1, detail: "" },
      { rule: "RAW_BUTTON", file: "b.vue", line: 1, detail: "" },
      { rule: "RAW_BUTTON", file: "c.vue", line: 9, detail: "" },
      { rule: "HARDCODED_RADIUS", file: "a.vue", line: 2, detail: "" },
    ]);
    const result = compare(now, baseline);
    expect(result.ok).toBe(false);
    expect(result.messages.join("\n")).toContain("RAW_BUTTON: 3, baseline 2");
    expect(result.messages.join("\n")).toContain("c.vue: 0 → 1");
  });

  it("passes when a count falls, and asks for the baseline to be lowered", () => {
    const now = countsOf([
      { rule: "RAW_BUTTON", file: "a.vue", line: 1, detail: "" },
      { rule: "HARDCODED_RADIUS", file: "a.vue", line: 2, detail: "" },
    ]);
    const result = compare(now, baseline);
    expect(result.ok).toBe(true);
    expect(result.messages.join("\n")).toMatch(/RAW_BUTTON: 1, baseline 2 .*--update/);
  });

  it("a move between files with the same total passes", () => {
    const now = countsOf([
      { rule: "RAW_BUTTON", file: "a.vue", line: 1, detail: "" },
      { rule: "RAW_BUTTON", file: "a.vue", line: 5, detail: "" },
      { rule: "HARDCODED_RADIUS", file: "a.vue", line: 2, detail: "" },
    ]);
    expect(compare(now, baseline).ok).toBe(true);
  });
});
