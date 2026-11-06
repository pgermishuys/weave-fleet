import { describe, expect, it } from "vitest";
import { jobGroups, jobIdFromPath, jobPath, type Job } from "@/lib/jobs";

const job = (id: string, state: Job["state"]): Job => ({
  id, title: id, ask: "", repository: "", labels: [], pinnedMachineId: null, state, machineId: null, machineName: null,
  sessionId: null, whyWaiting: null, error: null, createdAt: "2026-10-27T08:00:00Z",
});

describe("jobs list", () => {
  it("groups every state exactly once", () => {
    const states = ["waiting", "running", "needs_you", "done", "failed"] as const;
    const groups = jobGroups(states.map((state) => job(state, state)));

    expect(groups.map((g) => [g.id, g.jobs.map((j) => j.id)])).toEqual([
      ["running", ["running", "needs_you"]],
      ["queued", ["waiting"]],
      ["done", ["done", "failed"]],
    ]);
  });

  it("round-trips a job through its route", () => {
    expect(jobPath("a b")).toBe("/jobs/a%20b");
    expect(jobIdFromPath("/jobs/a%20b")).toBe("a b");
    expect(jobIdFromPath("/jobs")).toBeNull();
    expect(jobIdFromPath("/jobs/")).toBeNull();
  });
});
