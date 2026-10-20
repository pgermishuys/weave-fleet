import { describe, expect, it } from "vitest";
import type { DesktopUpdateState } from "@/lib/desktop";
import {
  describeDesktopUpdate,
  describeMachineUpdate,
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
    return { key, name: key, kind: "node", os: null, version: "0.59.0", latest, where: key, isLive: false, state };
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
