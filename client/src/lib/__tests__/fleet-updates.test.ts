import { describe, expect, it } from "vitest";
import type { DesktopUpdateState } from "@/lib/desktop";
import {
  describeDesktopUpdate,
  busySessions,
  describeMachineUpdate,
  describeReleaseLine,
  describeRolloutNarrative,
  describeUpdateChip,
  describeVersionLine,
  newestCheckedAt,
  describeUpdatedNotice,
  effectiveRule,
  parseRuleOverrides,
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
    expect(machine({ status: "staged", stagedVersion: "0.59.1" }, "0.59.0", 2).label).toBe("0.59.1 downloaded · busy: 2 sessions working or waiting for you");
    expect(machine({ status: "staged", stagedVersion: "0.59.1" }, "0.59.0", 1)).toMatchObject({ kind: "busy", label: "0.59.1 downloaded · busy: 1 session working or waiting for you" });
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
    return { key, name: key, kind: "node", os: null, version: "0.59.0", latest, where: key, isLive: false, state, restart: null, isHome: false, rule: null };
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

describe("a session waiting on an answer", () => {
  it("keeps the row busy like a working one", () => {
    expect(busySessions({ working: 0, needsYou: 1 })).toBe(1);
    expect(busySessions({ working: 2, needsYou: 1 })).toBe(3);
    expect(busySessions(null)).toBe(0);
    const row = machine({ status: "staged", stagedVersion: "0.59.1" }, "0.59.0", busySessions({ working: 0, needsYou: 1 }));
    expect(row).toMatchObject({ kind: "busy", label: "0.59.1 downloaded · busy: 1 session working or waiting for you" });
  });
});

describe("update rules", () => {
  it("a machine follows its own rule, then the one chosen for all, then what suits its kind", () => {
    expect(effectiveRule(null, null, "node")).toBe("idle");
    expect(effectiveRule(null, null, "fleet")).toBe("ask");
    expect(effectiveRule(null, "overnight", "node")).toBe("overnight");
    expect(effectiveRule("ask", "overnight", "node")).toBe("ask");
  });

  it("keeps only the overrides that name a known rule", () => {
    expect(parseRuleOverrides('{"falcon":"idle","heron":"whenever","kestrel":3}')).toEqual({ falcon: "idle" });
    expect(parseRuleOverrides("not json")).toEqual({});
    expect(parseRuleOverrides(undefined)).toEqual({});
  });
});

describe("a Fleet that updated itself", () => {
  const now = new Date("2026-10-20T12:00:00Z");
  const last = { from: "0.59.0", to: "0.59.1", at: "2026-10-20T03:12:00Z", trigger: "idle" as const };

  it("says so for the machine and version it is running now", () => {
    const news = describeUpdatedNotice("falcon-id", "falcon", "0.59.1", last, now);

    expect(news?.title).toBe("falcon updated to 0.59.1");
    expect(news?.id).toBe("fleet-updated:falcon-id:0.59.1");
    expect(news?.body).toMatch(/^At \d{2}:\d{2}, when nothing was working\.$/);
  });

  it("stays quiet when it runs another version, has no record, or updated days ago", () => {
    expect(describeUpdatedNotice("f", "falcon", "0.59.0", last, now)).toBeNull();
    expect(describeUpdatedNotice("f", "falcon", "0.59.1", null, now)).toBeNull();
    expect(describeUpdatedNotice("f", "falcon", "0.59.1", last, new Date("2026-10-30T12:00:00Z"))).toBeNull();
  });

  it("names a different notice for the next version", () => {
    const next = describeUpdatedNotice("falcon-id", "falcon", "0.60.0", { ...last, to: "0.60.0" }, now);
    expect(next?.id).toBe("fleet-updated:falcon-id:0.60.0");
  });
});

function mrow(patch: Partial<FleetUpdateRow> & { state: FleetUpdateRow["state"] }): FleetUpdateRow {
  return { key: "falcon-id", name: "falcon", kind: "node", os: null, version: "0.59.0", latest: "0.59.1", where: "falcon", isLive: false, restart: null, isHome: false, rule: { effective: "idle", overridden: null }, ...patch };
}

function rollout(steps: Partial<RolloutStep>[], status: Rollout["status"] = "running"): Rollout {
  return {
    id: "r1",
    status,
    startedAt: "2026-10-20T09:00:00Z",
    steps: steps.map((step, i) => ({ machineId: `m${i}`, name: `m${i}`, state: "pending", waitingSessions: 0, ...step })),
  };
}

describe("the version line on a machine card", () => {
  const ready = machine({ status: "staged", stagedVersion: "0.59.1" });

  it("says a downloaded update waits for the machine's rule", () => {
    const idle = describeVersionLine(mrow({ state: machine({ status: "staged", stagedVersion: "0.59.1" }, "0.59.0", 2), working: 2 }), null);
    expect(idle).toMatchObject({ version: "Fleet 0.59.0", target: "0.59.1", text: "0.59.1 downloaded · updates itself when its 2 sessions finish", tone: "warn", ruleMenu: true });
    expect(describeVersionLine(mrow({ state: machine({ status: "staged", stagedVersion: "0.59.1" }, "0.59.0", 1), working: 1 }), null).text).toContain("when its session finishes");
    expect(describeVersionLine(mrow({ state: ready }), null).text).toBe("0.59.1 downloaded · updates itself in a minute");
    expect(describeVersionLine(mrow({ state: ready, rule: { effective: "overnight", overridden: "overnight" } }), null).text).toBe("0.59.1 downloaded · updates itself tonight, 02:00–05:00");
    expect(describeVersionLine(mrow({ state: ready, rule: { effective: "ask", overridden: null } }), null).text).toBe("0.59.1 downloaded · waiting for you to restart it");
  });

  it("puts the reason a machine can't restart itself in the line", () => {
    const line = describeVersionLine(mrow({ state: ready, restart: { kind: "unavailable", reason: "managed by systemd" } }), null);
    expect(line.text).toBe("0.59.1 downloaded · managed by systemd");
  });

  it("says where a rollout has the machine", () => {
    const run = rollout([{ name: "heron", state: "done" }, { machineId: "falcon-id", name: "falcon", state: "waiting", waitingSessions: 2 }, { name: "kestrel", state: "pending" }]);
    expect(describeVersionLine(mrow({ state: ready }), run).text).toBe("Updating: waiting for 2 sessions to finish");
    const next = rollout([{ name: "falcon", state: "waiting" }, { machineId: "falcon-id", name: "kestrel", state: "pending" }]);
    expect(describeVersionLine(mrow({ state: ready }), next).text).toBe("Updating: next, after falcon");
    const queued = rollout([{ name: "falcon", state: "waiting" }, { name: "heron", state: "pending" }, { machineId: "falcon-id", name: "kestrel", state: "pending" }]);
    expect(describeVersionLine(mrow({ state: ready }), queued).text).toBe("Updating: next, after heron");
    expect(describeVersionLine(mrow({ state: ready }), rollout([{ machineId: "falcon-id", state: "pending" }])).text).toBe("Updating: waiting its turn");
    expect(describeVersionLine(mrow({ state: ready }), rollout([{ machineId: "falcon-id", state: "restarting" }])).text).toBe("Updating: restarting…");
    expect(describeVersionLine(mrow({ state: ready }), rollout([{ machineId: "falcon-id", state: "done", toVersion: "0.59.1" }]))).toMatchObject({ version: "Fleet 0.59.1", text: "updated to 0.59.1 · healthy", tone: "ok" });
  });

  it("says when it can't be reached, with the version last seen and no rule menu", () => {
    const line = describeVersionLine(mrow({ version: "0.58.2", state: machine({}, "0.58.2", 0, false) }), null);
    expect(line).toMatchObject({ version: "Fleet 0.58.2", text: "can't be reached · last seen on 0.58.2", ruleMenu: false });
  });

  it("says up to date, and reuses the other states' words", () => {
    expect(describeVersionLine(mrow({ version: "0.59.1", state: machine({}, "0.59.1") }), null)).toMatchObject({ version: "Fleet 0.59.1", target: null, text: "up to date", tone: "ok" });
    expect(describeVersionLine(mrow({ state: machine({ status: "downloading" }) }), null).text).toBe("downloading 0.59.1…");
    expect(describeVersionLine(mrow({ state: machine({ status: "available" }) }), null).text).toBe("0.59.1 is out, not downloaded yet");
  });

  it("offers no rule menu for a Fleet too old to take one", () => {
    expect(describeVersionLine(mrow({ rule: null, state: machine({}, "0.59.1") }), null).ruleMenu).toBe(false);
  });
});

describe("the release line", () => {
  it("says how many machines have the release downloaded", () => {
    expect(describeReleaseLine({ latest: "0.59.1", waiting: 3, downloaded: 3 }, null)).toEqual({ kind: "available", lead: "Fleet 0.59.1 is out.", text: "3 machines have it downloaded and switch when they restart." });
    expect(describeReleaseLine({ latest: "0.59.1", waiting: 1, downloaded: 1 }, null).text).toBe("1 machine has it downloaded and switches when it restarts.");
    expect(describeReleaseLine({ latest: "0.59.1", waiting: 3, downloaded: 2 }, null).text).toBe("2 of 3 machines that can update have it downloaded and switch when they restart.");
    expect(describeReleaseLine({ latest: "0.59.1", waiting: 2, downloaded: 0 }, null).text).toBe("2 machines can update; they aren't downloaded yet.");
  });

  it("is one quiet line when there is nothing to do, or no release", () => {
    const now = Date.parse("2026-10-20T12:00:00Z");
    expect(describeReleaseLine({ latest: "0.59.1", waiting: 0, downloaded: 0 }, "2026-10-20T11:56:00Z", now))
      .toEqual({ kind: "current", lead: "", text: "Every machine that can be reached is on Fleet 0.59.1. Checked 4m ago." });
    expect(describeReleaseLine({ latest: null, waiting: 0, downloaded: 0 }, null)).toMatchObject({ kind: "none", text: "Fleet hasn't found a release yet." });
  });

  it("finds the newest check across machines", () => {
    expect(newestCheckedAt([null, "2026-10-20T08:00:00Z", undefined, "2026-10-20T11:00:00Z", "nonsense"])).toBe("2026-10-20T11:00:00Z");
    expect(newestCheckedAt([null])).toBeNull();
  });
});

describe("the rollout narrative", () => {
  it("tells each machine's part in a sentence", () => {
    const run = rollout([
      { name: "heron", state: "done", toVersion: "0.59.1" },
      { name: "falcon", state: "waiting", waitingSessions: 2 },
      { name: "kestrel", state: "pending" },
      { name: "osprey", state: "skipped", detail: "can't be reached" },
    ]);
    expect(describeRolloutNarrative(run)).toEqual({
      title: "Updating machines to 0.59.1, one at a time.",
      active: "falcon",
      text: "heron is done. falcon is waiting for 2 sessions; kestrel is next. osprey can't be reached, so it's skipped.",
    });
  });

  it("has nobody active before the first machine starts", () => {
    expect(describeRolloutNarrative(rollout([{ name: "a" }, { name: "b" }])).active).toBeNull();
  });

  it("says a machine that can't be reached will be skipped, and the next one is the next reachable machine", () => {
    const run = rollout([{ name: "falcon", state: "waiting", waitingSessions: 1 }, { machineId: "osprey-id", name: "osprey" }, { name: "heron" }]);
    expect(describeRolloutNarrative(run, new Set(["osprey-id"])).text).toBe("falcon is waiting for 1 session. osprey can't be reached, so it'll be skipped. heron is next.");
  });
});

describe("the status bar chip", () => {
  it("counts the machines that can update", () => {
    expect(describeUpdateChip(0)).toBeNull();
    expect(describeUpdateChip(1)).toBe("↑ 1 machine can update");
    expect(describeUpdateChip(3)).toBe("↑ 3 machines can update");
  });

  it("says where a rollout is while it runs, one step ahead of the finished ones and never past the last", () => {
    const steps: Partial<RolloutStep>[] = [{ state: "done" }, { state: "waiting" }, { state: "pending" }];
    expect(describeUpdateChip(3, rollout(steps))).toBe("Updating machines · 2 of 3");
    expect(describeUpdateChip(0, rollout([{ state: "done" }, { state: "skipped" }, { state: "done" }]))).toBe("Updating machines · 3 of 3");
    expect(describeUpdateChip(2, rollout(steps, "done"))).toBe("↑ 2 machines can update");
  });
});
