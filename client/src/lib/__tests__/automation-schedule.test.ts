import { describe, expect, it } from "vitest";
import {
  autoName,
  cronOf,
  fromTrigger,
  nextRun,
  nextShort,
  parseSchedule,
  promptFrom,
  toTrigger,
  whenChip,
  whenSentence,
  type When,
} from "@/lib/automation-schedule";

// Tuesday 15 September 2026, 10:00 on the test's clock.
const NOW = new Date(2026, 8, 15, 10, 0, 0);

function whenOf(text: string): When | null {
  return parseSchedule(text)?.when ?? null;
}

describe("parseSchedule", () => {
  it.each<[string, When]>([
    ["Every Monday at 9am, summarise the open PRs", { kind: "weekly", days: [1], t: [9, 0] }],
    ["Weekdays at 8:30, list yesterday's failed CI runs", { kind: "weekly", days: [1, 2, 3, 4, 5], t: [8, 30] }],
    ["Every 2 hours, check whether the nightly E2E run is flaky", { kind: "hours", n: 2 }],
    ["Every 15 minutes, ping the preview", { kind: "minutes", n: 15 }],
    ["Hourly, look at the queue", { kind: "hours", n: 1 }],
    ["On the 1st of every month at 10, prune worktrees", { kind: "monthly", dom: 1, t: [10, 0] }],
    ["Every Tuesday and Thursday at 4pm, triage issues", { kind: "weekly", days: [2, 4], t: [16, 0] }],
    ["Every day at noon, post a summary", { kind: "weekly", days: [0, 1, 2, 3, 4, 5, 6], t: [12, 0] }],
    ["Nightly, check for outdated packages", { kind: "weekly", days: [0, 1, 2, 3, 4, 5, 6], t: [21, 0] }],
    ["Mondays at 7:45, plan the week", { kind: "weekly", days: [1], t: [7, 45] }],
    ["Create an automation that runs once on a monday to check the release notes", { kind: "once", day: 1, t: [9, 0] }],
    ["Tomorrow at 3pm, remind me to review #212", { kind: "once", rel: "tomorrow", t: [15, 0] }],
    ["Next Friday, tidy the changelog", { kind: "once", day: 5, t: [9, 0] }],
    ["Tidy the changelog on Friday.", { kind: "weekly", days: [5], t: [9, 0], ambiguous: true }],
    ["Tidy the changelog on a Friday.", { kind: "weekly", days: [5], t: [9, 0] }],
  ])("reads %j", (text, expected) => {
    expect(whenOf(text)).toEqual(expected);
  });

  it("finds nothing in a sentence without a schedule", () => {
    expect(parseSchedule("Summarise the open PRs")).toBeNull();
  });

  it("says where the schedule words are, so they can be highlighted", () => {
    const text = "Please, every Monday at 9, summarise the open PRs";
    const hit = parseSchedule(text)!;
    expect(text.slice(hit.start, hit.end)).toBe("every Monday at 9");
  });
});

describe("promptFrom", () => {
  it("leaves out the schedule words", () => {
    const text = "Every Monday at 9am, go through the open pull requests and summarise each one.";
    expect(promptFrom(text, parseSchedule(text))).toBe("Go through the open pull requests and summarise each one.");
  });

  it("leaves out \"create an automation that…\", which is meant for Fleet", () => {
    const text = "Create an automation that runs once on a monday to check whether the v0.24 release notes cover everything.";
    expect(promptFrom(text, parseSchedule(text))).toBe("Check whether the v0.24 release notes cover everything.");
  });

  it("leaves a prompt with nothing to take out as it was written", () => {
    expect(promptFrom("summarise the open PRs:", null)).toBe("summarise the open PRs:");
  });

  it("keeps line breaks in a longer prompt", () => {
    const text = "Every day at 9, check CI.\nThen post the result.";
    expect(promptFrom(text, parseSchedule(text))).toBe("Check CI.\nThen post the result.");
  });
});

describe("autoName", () => {
  it("takes the first words of the first sentence", () => {
    expect(autoName("Go through the open pull requests in weave-fleet and summarise each one.")).toBe("Go through the open pull requests");
  });
});

describe("nextRun", () => {
  it("finds the next matching day and time", () => {
    expect(nextRun({ kind: "weekly", days: [1], t: [9, 0] }, NOW)).toEqual(new Date(2026, 8, 21, 9, 0));
    expect(nextRun({ kind: "weekly", days: [2], t: [11, 0] }, NOW)).toEqual(new Date(2026, 8, 15, 11, 0));
    expect(nextRun({ kind: "hours", n: 2 }, NOW)).toEqual(new Date(2026, 8, 15, 12, 0));
    expect(nextRun({ kind: "monthly", dom: 1, t: [10, 0] }, NOW)).toEqual(new Date(2026, 9, 1, 10, 0));
  });

  it("puts a one-off on the next such day, or the date it was fixed to", () => {
    expect(nextRun({ kind: "once", day: 1, t: [9, 0] }, NOW)).toEqual(new Date(2026, 8, 21, 9, 0));
    expect(nextRun({ kind: "once", rel: "tomorrow", t: [15, 0] }, NOW)).toEqual(new Date(2026, 8, 16, 15, 0));
    expect(nextRun({ kind: "once", date: "2026-10-02", t: [8, 30] }, NOW)).toEqual(new Date(2026, 9, 2, 8, 30));
  });
});

describe("triggers", () => {
  it.each<[When, string, string]>([
    [{ kind: "weekly", days: [1], t: [9, 0] }, "schedule", "0 9 * * 1"],
    [{ kind: "weekly", days: [1, 2, 3, 4, 5], t: [8, 30] }, "schedule", "30 8 * * 1-5"],
    [{ kind: "weekly", days: [0, 1, 2, 3, 4, 5, 6], t: [21, 0] }, "schedule", "0 21 * * *"],
    [{ kind: "weekly", days: [2, 4], t: [16, 0] }, "schedule", "0 16 * * 2,4"],
    [{ kind: "hours", n: 1 }, "schedule", "0 * * * *"],
    [{ kind: "hours", n: 2 }, "schedule", "0 */2 * * *"],
    [{ kind: "minutes", n: 15 }, "schedule", "*/15 * * * *"],
    [{ kind: "minutes", n: 1 }, "schedule", "* * * * *"],
    [{ kind: "monthly", dom: 1, t: [10, 0] }, "schedule", "0 10 1 * *"],
    [{ kind: "once", day: 1, t: [9, 0] }, "once", "2026-09-21T09:00"],
    [{ kind: "event", eventType: "session_created" }, "event", "{\"eventType\":\"session_created\"}"],
  ])("stores %j as the trigger the server reads, and reads it back", (when, triggerType, triggerConfig) => {
    expect(toTrigger(when, NOW)).toEqual({ triggerType, triggerConfig });
    const back = fromTrigger(triggerType, triggerConfig)!;
    expect(toTrigger(back, NOW)).toEqual({ triggerType, triggerConfig });
  });

  it("keeps a cron it has no words for as Custom", () => {
    expect(fromTrigger("schedule", "0 9 1-7 * 1")).toEqual({ kind: "cron", expr: "0 9 1-7 * 1" });
    expect(cronOf({ kind: "cron", expr: "0 9 1-7 * 1" })).toBe("0 9 1-7 * 1");
  });
});

describe("words", () => {
  it("labels the When chip and the line under the box", () => {
    expect(whenChip({ kind: "weekly", days: [1], t: [9, 0] }, NOW)).toBe("Mondays 09:00");
    expect(whenChip({ kind: "once", day: 1, t: [9, 0] }, NOW)).toBe("Once · Mon 21 Sep");
    expect(whenChip(null, NOW)).toBe("When?");
    expect(whenSentence({ kind: "weekly", days: [1, 2, 3, 4, 5], t: [8, 30] }, NOW)).toBe("on weekdays at 08:30");
    expect(whenSentence({ kind: "once", day: 1, t: [9, 0] }, NOW)).toBe("once, on Mon 21 Sep at 09:00");
  });

  it("shortens a sidebar row's next run by how far away it is", () => {
    expect(nextShort(new Date(2026, 8, 15, 17, 0), NOW)).toBe("17:00");
    expect(nextShort(new Date(2026, 8, 21, 9, 0), NOW)).toBe("Mon 09:00");
    expect(nextShort(new Date(2026, 9, 1, 10, 0), NOW)).toBe("1 Oct");
  });
});
