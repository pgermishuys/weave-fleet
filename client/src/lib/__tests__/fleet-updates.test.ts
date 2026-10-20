import { describe, expect, it } from "vitest";
import type { DesktopUpdateState } from "@/lib/desktop";
import {
  describeDesktopUpdate,
  describeMachineUpdate,
  describeRolloutOutcome,
  describeRolloutStep,
  restartOffer,
  type Rollout,
  type RolloutStep,
  hasUpdateWaiting,
  isNewerVersion,
  summarizeRows,
  type FleetUpdateRow,
  type MachineUpdate,
} from "@/lib/fleet-updates";

function update(patch: Partial<MachineUpdate>): MachineUpdate {
  return { status: "uptodate", latestVersion: "0.59.1", stagedVersion: null, checkedAt: "2026-10-20T08:00:00Z", ...patch };
}

function machine(patch: Partial<MachineUpdate>, version = "0.59.0", working = 0, reachable = true) {
  return describeMachineUpdate({ version, update: update(patch), working, reachable });
}

describe("isNewerVersion", () => {
  it("compares numerically, not as text", () => {
    expect(isNewerVersion("0.59.1", "0.59.0")).toBe(true);
    expect(isNewerVersion("0.60.0", "0.59.9")).toBe(true);
    expect(isNewerVersion("0.9.0", "0.10.0")).toBe(false);
    expect(isNewerVersion("0.59.0", "0.59.0")).toBe(false);
    expect(isNewerVersion(null, "0.59.0")).toBe(false);
  });
});

describe("describeMachineUpdate", () => {
  it("says up to date when it runs the newest release", () => {
    expect(machine({}, "0.59.1")).toMatchObject({ kind: "current", label: "up to date", tone: "ok", target: null });
  });

  it("says an update is out when it hasn't downloaded it", () => {
    expect(machine({ status: "available" })).toMatchObject({ kind: "available", target: "0.59.1" });
  });

  it("says it's downloading", () => {
    expect(machine({ status: "downloading" })).toMatchObject({ kind: "downloading", label: "downloading 0.59.1…" });
  });

  it("says it's downloaded and waiting for a restart", () => {
    expect(machine({ status: "staged", stagedVersion: "0.59.1" })).toMatchObject({
      kind: "ready",
      label: "0.59.1 downloaded · waiting for a restart",
      target: "0.59.1",
    });
  });

  it("knows about a download staged before the last restart even though it hasn't checked since", () => {
    expect(machine({ status: "unknown", latestVersion: null, stagedVersion: "0.59.1" })).toMatchObject({ kind: "ready", target: "0.59.1" });
  });

  it("says busy, with how many sessions are working, when the download waits on a busy machine", () => {
    expect(machine({ status: "staged", stagedVersion: "0.59.1" }, "0.59.0", 2).label).toBe("0.59.1 downloaded · busy: 2 sessions working");
    expect(machine({ status: "staged", stagedVersion: "0.59.1" }, "0.59.0", 1)).toMatchObject({ kind: "busy", label: "0.59.1 downloaded · busy: 1 session working" });
  });

  it("says it can't be reached, whatever it last said", () => {
    expect(machine({ status: "staged", stagedVersion: "0.59.1" }, "0.59.0", 0, false)).toMatchObject({ kind: "unreachable", label: "can't be reached" });
  });

  it("says the desktop app manages it", () => {
    expect(machine({ status: "managed", latestVersion: null })).toMatchObject({ kind: "managed", label: "managed by the desktop app" });
  });

  it("says a failed check, and one that hasn't run", () => {
    expect(machine({ status: "error", latestVersion: null })).toMatchObject({ kind: "error", tone: "bad" });
    expect(machine({ status: "unknown", latestVersion: null })).toMatchObject({ kind: "unchecked" });
  });

  it("says a Fleet too old to report updates", () => {
    expect(describeMachineUpdate({ version: "0.50.0", update: null, working: 0, reachable: true })).toMatchObject({ kind: "unsupported" });
  });
});

describe("describeDesktopUpdate", () => {
  function app(patch: Partial<DesktopUpdateState>): DesktopUpdateState {
    return { status: "idle", mode: "install", currentVersion: "0.59.0", ...patch };
  }

  it("maps each state the app reports", () => {
    expect(describeDesktopUpdate(app({}))).toMatchObject({ kind: "current" });
    expect(describeDesktopUpdate(app({ status: "checking" }))).toMatchObject({ kind: "checking" });
    expect(describeDesktopUpdate(app({ status: "downloading", version: "0.59.1", percent: 40 }))).toMatchObject({ kind: "downloading", label: "downloading 0.59.1… 40%" });
    expect(describeDesktopUpdate(app({ status: "ready", version: "0.59.1" }))).toMatchObject({ kind: "ready", target: "0.59.1" });
    expect(describeDesktopUpdate(app({ status: "error" }))).toMatchObject({ kind: "error" });
    expect(describeDesktopUpdate(app({ status: "off", mode: "off" }))).toMatchObject({ kind: "unsupported" });
    expect(describeDesktopUpdate(null)).toMatchObject({ kind: "unchecked" });
  });

  it("offers a download, not an install, for an app that can't update itself", () => {
    expect(describeDesktopUpdate(app({ status: "available", mode: "notify", version: "0.59.1" }))).toMatchObject({ kind: "manual", target: "0.59.1" });
  });
});

describe("summarizeRows", () => {
  function row(key: string, state: FleetUpdateRow["state"], latest: string | null = "0.59.1"): FleetUpdateRow {
    return { key, name: key, kind: "node", os: null, version: "0.59.0", latest, where: key, isLive: false, state, restart: null, isHome: false };
  }

  it("counts the Fleets with an update waiting, the ones that have it downloaded, and the ones it can't reach", () => {
    const rows = [
      row("kestrel", machine({}, "0.59.1")),
      row("falcon", machine({ status: "staged", stagedVersion: "0.59.1" })),
      row("heron", machine({ status: "staged", stagedVersion: "0.59.1" }, "0.59.0", 2)),
      row("osprey", machine({}, "0.59.0", 0, false)),
      row("wren", machine({ status: "available" })),
    ];

    expect(summarizeRows(rows)).toEqual({ latest: "0.59.1", waiting: 3, downloaded: 2, unreachable: 1 });
    expect(rows.map((r) => hasUpdateWaiting(r.state))).toEqual([false, true, true, false, true]);
  });
});

describe("restart offers", () => {
  const staged = update({ status: "staged", stagedVersion: "0.59.1", canRestart: true });

  it("offers a button on a machine that has the update and can restart", () => {
    const state = machine({ status: "staged", stagedVersion: "0.59.1", canRestart: true });
    expect(restartOffer(state, staged, false)).toEqual({ kind: "button", label: "Restart into 0.59.1", disabled: false });
  });

  it("lets a busy machine in line, since the rollout waits for its sessions, but not this one", () => {
    const state = machine({ status: "staged", stagedVersion: "0.59.1", canRestart: true }, "0.59.0", 2);
    expect(restartOffer(state, staged, false)).toEqual({ kind: "button", label: "Restart when idle", disabled: false });
    expect(restartOffer(state, staged, true)).toMatchObject({ kind: "button", disabled: true });
  });

  it("says why a machine can't restart itself instead of offering a button", () => {
    const state = machine({ status: "staged", stagedVersion: "0.59.1", canRestart: false, restartUnavailableReason: "Restart Fleet once by hand; after that it can restart itself." });
    expect(restartOffer(state, update({ canRestart: false, restartUnavailableReason: "Restart Fleet once by hand; after that it can restart itself." }), false))
      .toEqual({ kind: "unavailable", reason: "Restart Fleet once by hand; after that it can restart itself." });
    expect(restartOffer(state, update({}), false)).toMatchObject({ kind: "unavailable", reason: expect.stringContaining("too old") });
  });

  it("offers nothing to a machine with no update waiting", () => {
    expect(restartOffer(machine({}, "0.59.1"), update({ canRestart: true }), false)).toBeNull();
  });

  it("reports an update that was undone, until nothing newer is on its way", () => {
    const state = machine({ rolledBack: { from: "0.59.0", to: "0.59.2", reason: "it exited with code 1 before it was ready", at: "2026-10-20T09:00:00Z" } }, "0.59.0");
    expect(state).toMatchObject({ kind: "rolledback", tone: "bad" });
    expect(state.label).toContain("0.59.2 didn't start here");
    expect(hasUpdateWaiting(state)).toBe(false);
    // A newer release staged since is shown as waiting; it isn't the one that failed.
    expect(machine({ status: "staged", stagedVersion: "0.59.3", rolledBack: { from: "0.59.0", to: "0.59.2", reason: "x", at: "y" } }, "0.59.0").kind).toBe("ready");
  });
});

describe("rollout progress", () => {
  function step(patch: Partial<RolloutStep>): RolloutStep {
    return { machineId: "m1", name: "falcon", state: "pending", waitingSessions: 0, ...patch };
  }

  it("maps each step to the state its row shows", () => {
    expect(describeRolloutStep(step({ state: "waiting", detail: "waiting for 2 sessions to finish", toVersion: "0.59.1" })))
      .toMatchObject({ kind: "waiting", label: "waiting for 2 sessions to finish", tone: "warn" });
    expect(describeRolloutStep(step({ state: "restarting", detail: "restarting into 0.59.1" }))).toMatchObject({ kind: "restarting", tone: "info" });
    expect(describeRolloutStep(step({ state: "done", toVersion: "0.59.1" }))).toMatchObject({ kind: "updated", label: "updated to 0.59.1 · healthy", tone: "ok" });
    expect(describeRolloutStep(step({ state: "skipped", detail: "can't be reached" }))).toMatchObject({ kind: "skipped", label: "skipped · can't be reached" });
    expect(describeRolloutStep(step({ state: "pending" }))).toMatchObject({ kind: "pending", label: "waiting its turn" });
  });

  it("says a failed machine stopped the rollout", () => {
    expect(describeRolloutStep(step({ state: "rolled-back", detail: "went back to 0.59.0: it exited with code 1 before it was ready" })).label).toContain("the rollout stopped here");
    expect(describeRolloutStep(step({ state: "no-return", detail: "didn't come back within 3 minutes" }))).toMatchObject({ kind: "noreturn", tone: "bad" });
  });

  function run(status: Rollout["status"], steps: RolloutStep[], summary: string | null = null): Rollout {
    return { id: "r1", status, startedAt: "2026-10-20T09:00:00Z", steps, summary };
  }

  it("has no outcome while it runs, and names who was updated or skipped once it is done", () => {
    expect(describeRolloutOutcome(run("running", [step({})]))).toBeNull();
    expect(describeRolloutOutcome(run("done", [step({ name: "heron", state: "done" }), step({ name: "falcon", state: "done" }), step({ name: "kite", state: "skipped" })])))
      .toEqual({ text: "Updated heron and falcon. Skipped kite.", tone: "ok" });
  });

  it("says why it stopped and who it left alone", () => {
    const outcome = describeRolloutOutcome(run("stopped", [step({ name: "heron", state: "rolled-back" }), step({ name: "falcon", state: "pending" })], "heron didn't start on 0.59.2 and went back to 0.59.0."));
    expect(outcome).toEqual({ text: "heron didn't start on 0.59.2 and went back to 0.59.0. falcon was left alone.", tone: "bad" });
  });
});
