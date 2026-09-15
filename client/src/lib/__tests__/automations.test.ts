import { describe, expect, it } from "vitest";
import { parseSchedule, promptFrom } from "@/lib/automation-schedule";
import { automationRowStatus, describeAutomationPlan, describeRunTrigger, type AutomationPlanInput } from "@/lib/automations";
import type { Automation } from "@/stores/automations";

// Tuesday 15 September 2026, 10:00.
const NOW = new Date(2026, 8, 15, 10, 0, 0);

function planFor(text: string, extra: Partial<AutomationPlanInput> = {}): string {
  const hit = parseSchedule(text);
  const plan = describeAutomationPlan({
    text,
    when: hit?.when ?? null,
    hit,
    prompt: promptFrom(text, hit),
    forceOnce: false,
    folder: { kind: "repository", path: "/home/me/source/weave-fleet" },
    worktree: true,
    base: "origin/main",
    sameSession: false,
    timeZone: "Europe/London",
    from: NOW,
    ...extra,
  });
  return [...plan.schedule, ...(plan.ask ? [{ text: ` ${plan.ask.question} [${plan.ask.answer}]` }] : []), { text: " " }, ...plan.rest]
    .map((part) => part.text)
    .join("");
}

describe("describeAutomationPlan", () => {
  it("says when it runs, where each run happens, and what the agent gets", () => {
    expect(planFor("Every Monday at 9, summarise the open PRs")).toBe(
      "Runs every Monday at 09:00 (Europe/London time), next Mon 21 Sep, 09:00. "
      + "Each run: a new session in a new worktree of ~/source/weave-fleet from origin/main. "
      + "The agent gets “Summarise the open PRs”",
    );
  });

  it("says a one-off switches itself off", () => {
    expect(planFor("Once on Friday at 4pm, tidy the changelog")).toContain("Runs once, on Fri 18 Sep at 16:00 (Europe/London time), then it switches off.");
  });

  it("asks about “on Friday”", () => {
    expect(planFor("Tidy the changelog on Friday")).toContain("Every Friday, or just once? [Just once]");
  });

  it("describes the folder as it is, no folder, and continuing one session", () => {
    expect(planFor("Daily at 8, check CI", { worktree: false })).toContain("Each run: a new session in ~/source/weave-fleet as it is.");
    expect(planFor("Daily at 8, check CI", { folder: { kind: "none" } })).toContain("Each run: a new session with no folder.");
    expect(planFor("Daily at 8, check CI", { folder: null })).toContain("Choose where it runs.");
    expect(planFor("Daily at 8, check CI", { sameSession: true })).toContain("Later runs continue the first run's session.");
  });

  it("asks for the schedule, or for what to do", () => {
    expect(planFor("Summarise the open PRs")).toContain("When should it run?");
    expect(planFor("Every Monday at 9")).toContain("Say what it should do.");
  });

  it("warns when saving moves a schedule to another time zone", () => {
    expect(planFor("Every Monday at 9, digest", { savedTimeZone: null })).toContain("Saving moves it from UTC time to Europe/London.");
    expect(planFor("Every Monday at 9, digest", { savedTimeZone: "Europe/London" })).not.toContain("Saving moves it");
  });
});

describe("automationRowStatus", () => {
  const base = {
    id: "a1",
    isEnabled: true,
    triggerType: "schedule",
    nextRunAt: new Date(2026, 8, 21, 9, 0).toISOString(),
    lastRun: null,
  } as unknown as Automation;

  it("shows the next run, Running, Failed or Off", () => {
    expect(automationRowStatus(base, NOW)).toEqual({ label: "Mon 09:00", tone: "quiet", glyph: null });
    expect(automationRowStatus({ ...base, lastRun: { state: "running" } } as Automation, NOW).label).toBe("Running");
    expect(automationRowStatus({ ...base, lastRun: { state: "failed" } } as Automation, NOW)).toEqual({ label: "Failed", tone: "error", glyph: "error" });
    expect(automationRowStatus({ ...base, isEnabled: false, nextRunAt: null }, NOW).label).toBe("Off");
    expect(automationRowStatus({ ...base, triggerType: "event", nextRunAt: null }, NOW).label).toBe("On event");
  });
});

describe("describeRunTrigger", () => {
  it("names what started a run, and nothing for the schedule", () => {
    expect(describeRunTrigger("schedule")).toBe("");
    expect(describeRunTrigger("catch_up")).toBe("Caught up");
    expect(describeRunTrigger("manual")).toBe("Run now");
    expect(describeRunTrigger("session_created")).toBe("A session starts");
  });
});
