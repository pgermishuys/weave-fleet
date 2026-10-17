import { describe, expect, it } from "vitest";
import { changedFilesText, interruptionRowLabel, toSessionInterruption } from "@/lib/session-interruption";

describe("toSessionInterruption", () => {
  it("reads a full interruption", () => {
    expect(toSessionInterruption({ at: "2026-10-17T14:02:00Z", waiting: false, lastCall: "bash: bun run build", changedFiles: 3 })).toEqual({
      at: "2026-10-17T14:02:00Z",
      waiting: false,
      lastCall: "bash: bun run build",
      changedFiles: 3,
    });
  });

  it("fills in what is missing", () => {
    expect(toSessionInterruption({ at: "2026-10-17T14:02:00Z" })).toEqual({
      at: "2026-10-17T14:02:00Z",
      waiting: false,
      lastCall: null,
      changedFiles: null,
    });
  });

  it("is null without a usable time or an object", () => {
    expect(toSessionInterruption(null)).toBeNull();
    expect(toSessionInterruption("x")).toBeNull();
    expect(toSessionInterruption({ at: "not a time" })).toBeNull();
    expect(toSessionInterruption({ waiting: true })).toBeNull();
  });
});

describe("wording", () => {
  it("names the row by what was lost", () => {
    const base = { at: "2026-10-17T14:02:00Z", lastCall: null, changedFiles: null };
    expect(interruptionRowLabel({ ...base, waiting: false })).toBe("Cut off by a restart");
    expect(interruptionRowLabel({ ...base, waiting: true })).toBe("Cut off · question lost");
  });

  it("counts files", () => {
    expect(changedFilesText(1)).toBe("1 file changed in this worktree");
    expect(changedFilesText(4)).toBe("4 files changed in this worktree");
  });
});
