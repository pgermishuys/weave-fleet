import { describe, expect, test } from "bun:test";
import { matches, matcherToJson } from "../../src/runtime/match";

describe("matcher", () => {
  const e = { component: "ToolUse", props: { tool: "bash", status: "running", input: { command: "ls" } }, n: 3 };
  test("a value matches itself", () => {
    expect(matches({ component: "ToolUse" }, e)).toBe(true);
    expect(matches({ component: "ToolResult" }, e)).toBe(false);
    expect(matches({ n: 3 }, e)).toBe(true);
  });
  test("an array matches if any item matches", () => {
    expect(matches({ component: ["ToolResult", "ToolUse"] }, e)).toBe(true);
    expect(matches({ component: ["ToolResult", "Pane"] }, e)).toBe(false);
    expect(matches({ component: [/^Tool/] }, e)).toBe(true);
  });
  test("a RegExp tests a string and never a non-string", () => {
    expect(matches({ component: /^Tool(Use|Result)$/ }, e)).toBe(true);
    expect(matches({ component: /^Pane/ }, e)).toBe(false);
    expect(matches({ n: /3/ }, e)).toBe(false);
  });
  test("a RegExp with the g flag matches the same every time", () => {
    const re = /Tool/g;
    expect(matches({ component: re }, e)).toBe(true);
    expect(matches({ component: re }, e)).toBe(true);
  });
  test("a nested object matches recursively", () => {
    expect(matches({ props: { tool: ["bash", "shell"], input: { command: /^ls/ } } }, e)).toBe(true);
    expect(matches({ props: { tool: "read" } }, e)).toBe(false);
  });
  test("a missing field doesn't match", () => {
    expect(matches({ nothing: "x" }, e)).toBe(false);
    expect(matches({ props: { nothing: "x" } }, e)).toBe(false);
    expect(matches({ props: { tool: { deeper: "x" } } }, e)).toBe(false);
  });
  test("no matcher matches everything; an empty one too", () => {
    expect(matches(undefined, e)).toBe(true);
    expect(matches({}, e)).toBe(true);
  });
  test("matchers serialise with RegExp as $regex and flags", () => {
    expect(matcherToJson({ a: [/x/gi, "y"], b: { c: /z/ } })).toEqual({ a: [{ $regex: "x", flags: "gi" }, "y"], b: { c: { $regex: "z", flags: "" } } });
  });
});
