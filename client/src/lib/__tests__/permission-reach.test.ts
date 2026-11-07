import { describe, expect, it } from "vitest";
import { levelWith, reachOf } from "@/lib/permissions";

describe("a level as Edit files and Run commands", () => {
  it("reads each level as two rows", () => {
    expect(reachOf("ask")).toEqual({ edits: "ask", commands: "ask" });
    expect(reachOf("edits")).toEqual({ edits: "allowed", commands: "ask" });
    expect(reachOf("all")).toEqual({ edits: "allowed", commands: "allowed" });
  });

  it.each([
    ["all", "edits", "ask", "ask"],
    ["all", "commands", "ask", "edits"],
    ["edits", "edits", "ask", "ask"],
    ["edits", "commands", "allowed", "all"],
    ["ask", "edits", "allowed", "edits"],
    ["ask", "commands", "allowed", "all"],
  ] as const)("from %s, %s %s gives %s", (level, row, value, expected) => {
    expect(levelWith(level, row, value)).toBe(expected);
  });

  it("never lets commands run unasked while edits ask", () => {
    for (const level of ["ask", "edits", "all"] as const)
      for (const row of ["edits", "commands"] as const)
        for (const value of ["allowed", "ask"] as const) {
          const reach = reachOf(levelWith(level, row, value));
          expect(reach.edits === "ask" && reach.commands === "allowed").toBe(false);
          expect(reach[row]).toBe(value);
        }
  });
});
