import { expect, test } from "bun:test";
import { css, mix, parseBlock, parseColor, resolveVar } from "../../scripts/tokens-lib";
import { tokens } from "./tokens.generated";

const sample = `
:root {
  /* comment */
  --text: #e8e8ec;
  --accent: #6366f1;
  --primary: var(--accent);
  --idle: #f59e0b;
  --status-waiting: var(--idle);
}
:root[data-theme="light"] {
  --idle: #d97706;
}
:root[data-theme="nord"] {
  --idle: #000000;
}
`;

test("parseBlock reads only the named rule", () => {
  expect(parseBlock(sample, ":root")["--idle"]).toBe("#f59e0b");
  expect(parseBlock(sample, ':root[data-theme="light"]')).toEqual({ "--idle": "#d97706" });
  expect(() => parseBlock(sample, ".nope")).toThrow();
});

test("resolveVar follows chains and light inherits what it does not set", () => {
  const dark = parseBlock(sample, ":root");
  expect(resolveVar(dark, "--primary")).toBe("#6366f1");
  const light = { ...dark, ...parseBlock(sample, ':root[data-theme="light"]') };
  expect(resolveVar(light, "--status-waiting")).toBe("#d97706");
  expect(() => resolveVar({ "--a": "var(--a)" }, "--a")).toThrow();
});

test("colours parse and print", () => {
  expect(parseColor("#fff")).toEqual({ r: 255, g: 255, b: 255, a: 1 });
  expect(css(parseColor("rgba(26, 25, 24, 0.09)"))).toBe("rgba(26, 25, 24, 0.09)");
  expect(css(parseColor("#5B6EC7"))).toBe("#5b6ec7");
});

test("mix works like color-mix in srgb", () => {
  expect(css(mix(parseColor("#e8e8ec"), 9, null))).toBe("rgba(232, 232, 236, 0.09)");
  expect(css(mix(parseColor("#ffffff"), 50, parseColor("#000000")))).toBe("#808080");
  // over a translucent colour the alpha adds up: 0.4 + 0.6 * 0.075
  expect(mix(parseColor("#f59e0b"), 40, parseColor("rgba(255, 255, 255, 0.075)")).a).toBeCloseTo(0.445);
});

test("the generated file carries the derived colours", () => {
  expect(tokens.light.chrome).toBe("#F3F2EF");
  expect(tokens.dark.tint9).toBe("rgba(232, 232, 236, 0.09)");
  expect(tokens.light.waitingBg).toBe("#fdf8f3");
});
