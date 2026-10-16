import { describe, expect, it } from "vitest";
import { isChoiceLive, pageChoiceBar, pageChoiceRowTitle, type PageChoice } from "@/lib/page-choice";

const waiting: PageChoice = { callId: "c1", status: "waiting", at: "2026-10-16T12:00:00Z" };
const pick = { value: "a", summary: "Layout A" };

describe("isChoiceLive", () => {
  it("is live while the session is in a turn, not when idle or unknown", () => {
    for (const status of ["busy", "retry", "waiting_input", "delegating"]) expect(isChoiceLive(waiting, status)).toBe(true);
    expect(isChoiceLive(waiting, "idle")).toBe(false);
    expect(isChoiceLive(waiting, undefined)).toBe(false);
    expect(isChoiceLive({ ...waiting, status: "answered" }, "busy")).toBe(false);
  });
});

describe("pageChoiceBar", () => {
  it("waits, then shows the proposal", () => {
    expect(pageChoiceBar(waiting, true, null)).toMatchObject({ kind: "waiting", text: "The agent is waiting for your pick." });
    expect(pageChoiceBar(waiting, true, pick)).toMatchObject({ kind: "proposed", text: "You picked ", strong: "Layout A" });
  });

  it("shows an answer", () => {
    expect(pageChoiceBar({ ...waiting, status: "answered", summary: "Layout B", value: "b" }, false, null))
      .toMatchObject({ kind: "answered", strong: "Layout B", at: waiting.at });
  });

  it("says why it wasn't answered", () => {
    const text = (reason?: string, live = false) => pageChoiceBar({ ...waiting, status: "not-answered", reason }, live, null).text;
    expect(text("wrote instead")).toBe("Not answered. You wrote instead.");
    expect(text("skipped")).toBe("Not answered. You skipped it.");
    expect(text("stopped")).toBe("Not answered. The turn stopped.");
    expect(text("Fleet restarted")).toBe("Not answered. Fleet restarted.");
    expect(text("timed out")).toBe("Not answered after 24 hours.");
    expect(text(undefined)).toBe("Not answered.");
  });

  it("reads a waiting choice on an idle session as not answered", () => {
    expect(pageChoiceBar(waiting, false, pick)).toMatchObject({ kind: "not-answered", text: "Not answered." });
  });
});

describe("pageChoiceRowTitle", () => {
  const input = { waitForChoice: true, title: "Layouts for pocket-notes" };

  it("leaves other calls alone", () => {
    expect(pageChoiceRowTitle({ title: "x" }, "running", null)).toBeUndefined();
    expect(pageChoiceRowTitle(null, "running", null)).toBeUndefined();
  });

  it("follows the call", () => {
    expect(pageChoiceRowTitle(input, "running", null)).toBe("Waiting for your pick · Layouts for pocket-notes");
    expect(pageChoiceRowTitle(input, "completed", { choice: { answered: true, value: "a", summary: "Layout A" } })).toBe("Picked Layout A");
    expect(pageChoiceRowTitle(input, "completed", { choice: { answered: false, reason: "wrote instead" } })).toBe("Not picked · wrote instead");
    expect(pageChoiceRowTitle(input, "completed", { choice: { answered: false, reason: "skipped" } })).toBe("Not picked · you skipped it");
    expect(pageChoiceRowTitle(input, "error", null)).toBe("Not answered");
  });
});
