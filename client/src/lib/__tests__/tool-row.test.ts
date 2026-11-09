import { describe, expect, it } from "vitest";
import { formatCallDuration, groupReads, shellHeader } from "@/lib/tool-row";

describe("formatCallDuration", () => {
  it("says tenths under a second, then seconds, then minutes", () => {
    expect(formatCallDuration(40)).toBe("0.1s");
    expect(formatCallDuration(140)).toBe("0.1s");
    expect(formatCallDuration(900)).toBe("0.9s");
    expect(formatCallDuration(34_000)).toBe("34s");
    expect(formatCallDuration(125_000)).toBe("2m 5s");
  });
  it("is empty when nothing was timed", () => {
    expect(formatCallDuration(undefined)).toBe("");
    expect(formatCallDuration(-5)).toBe("");
  });
});

describe("shellHeader", () => {
  it("joins the command, exit code and time", () => {
    expect(shellHeader({ command: "bun test" }, "x", 0, 100)).toBe("bun test · exit 0 · 0.1s");
  });
  it("leaves out what the harness did not report", () => {
    expect(shellHeader({ command: "bun test\nmore" }, "x")).toBe("bun test");
    expect(shellHeader(undefined, "ls")).toBe("ls");
  });
});

describe("groupReads", () => {
  const read = (id: string, status = "Completed") => ({ id, kind: "read", status });
  const edit = (id: string) => ({ id, kind: "edit", status: "Completed" });
  it("collapses two or more completed reads in a row", () => {
    const entries = groupReads([read("a"), read("b"), read("c"), edit("d")]);
    expect(entries.map((entry) => entry.type)).toEqual(["reads", "tool"]);
    expect(entries[0]).toMatchObject({ type: "reads", tools: [{ id: "a" }, { id: "b" }, { id: "c" }] });
  });
  it("leaves a single read, a running read and a failed read as their own rows", () => {
    expect(groupReads([read("a")]).map((entry) => entry.type)).toEqual(["tool"]);
    expect(groupReads([read("a"), read("b", "Running")]).map((entry) => entry.type)).toEqual(["tool", "tool"]);
    expect(groupReads([read("a"), read("b", "Error"), read("c")]).map((entry) => entry.type)).toEqual(["tool", "tool", "tool"]);
  });
  it("works for any harness's read tool name", () => {
    const kinds = ["Read", "ls", "read"].map((kind, index) => ({ id: String(index), kind, status: "Completed" }));
    expect(groupReads(kinds).map((entry) => entry.type)).toEqual(["reads"]);
  });
});
