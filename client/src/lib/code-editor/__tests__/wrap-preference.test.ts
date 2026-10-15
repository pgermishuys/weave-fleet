import { beforeEach, describe, expect, it } from "vitest";
import { setWrapFor, WRAP_BY_DEFAULT, wrapFor } from "../wrap-preference";

describe("wrap preference", () => {
  beforeEach(() => localStorage.clear());

  it("wraps by default", () => {
    expect(WRAP_BY_DEFAULT).toBe(true);
    expect(wrapFor("src/app.ts")).toBe(true);
  });

  it("remembers a file turned off, and only that file", () => {
    setWrapFor("src/app.ts", false);
    expect(wrapFor("src/app.ts")).toBe(false);
    expect(wrapFor("src/other.ts")).toBe(true);
  });

  it("keeps only choices that differ from the default", () => {
    setWrapFor("src/app.ts", false);
    setWrapFor("src/app.ts", true);
    expect(wrapFor("src/app.ts")).toBe(true);
    expect(localStorage.getItem("fleet.editor.wrap")).toBe("{}");
  });

  it("keeps the latest 200 files", () => {
    for (let i = 0; i < 205; i++) setWrapFor(`src/file-${i}.ts`, false);
    expect(Object.keys(JSON.parse(localStorage.getItem("fleet.editor.wrap")!))).toHaveLength(200);
    expect(wrapFor("src/file-0.ts")).toBe(true);
    expect(wrapFor("src/file-204.ts")).toBe(false);
  });

  it("ignores a broken stored value", () => {
    localStorage.setItem("fleet.editor.wrap", "not json");
    expect(wrapFor("src/app.ts")).toBe(true);
  });
});
