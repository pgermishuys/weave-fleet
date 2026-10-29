import { computed, onMounted, onUnmounted, shallowRef, watch } from "vue";
import { getDesktopBridge } from "@/lib/desktop";
import {
  describeDesktopUpdate,
  describeMachineUpdate,
  describeRolloutOutcome,
  describeRolloutStep,
  busySessions,
  FLEET_UPDATES_PREFERENCE_KEY,
  hasUpdateWaiting,
  readLastSeenVersions,
  writeLastSeenVersions,
  osLabel,
  restartOffer,
  effectiveRule,
  isUpdateRule,
  newestCheckedAt,
  parseRuleOverrides,
  summarizeRows,
  UPDATE_RULE_DEFAULT_KEY,
  UPDATE_RULE_OVERRIDES_KEY,
  type FleetUpdateRow,
  type Rollout,
  type UpdateRule,
} from "@/lib/fleet-updates";
import { onDomainEvent } from "@/composables/on-domain-event";
import { liveTarget } from "@/lib/machine-target";
import { fetchOnMachine, HOME_MACHINE_KEY } from "@/lib/machines";
import { useDesktopUpdates } from "@/composables/use-desktop-updates";
import { useMachinesStore, type MachineEntry } from "@/stores/machines";
import { usePreferencesStore } from "@/stores/preferences";

/** While a check or a download is under way, the page asks again this often; otherwise the machine store's own poll is enough. */
const BUSY_REFRESH_MS = 3_000;

/** While a rollout runs, the page reads it this often as well as taking what the Fleet pushes (a push can be missed across a reconnect). */
const ROLLOUT_POLL_MS = 3_000;

/** After this Fleet restarts itself, the page asks whether it is back this often, for at most this long. */
const COME_BACK_POLL_MS = 1_500;
const COME_BACK_LIMIT_MS = 3 * 60_000;

const DESKTOP_PLATFORMS: Record<string, string> = { darwin: "macos", win32: "windows", linux: "linux" };

/** Settings → Labs: whether Settings → Updates & about shows each machine's version, update rule and Restart. */
export function useFleetUpdatesEnabled() {
  const preferences = usePreferencesStore();
  preferences.ensureLoaded();
  return computed(() => preferences.get(FLEET_UPDATES_PREFERENCE_KEY, "false") === "true");
}

/**
 * Every Fleet the user runs, with its version and update state: the desktop app when this page runs in it, then home
 * and each machine in Settings → Machines. Machines come from the machine store, so this adds no polling of its own:
 * the store keeps `GET /api/machine` fresh for every machine (a live feed or a poll, as Settings → Labs chose).
 */
export function useFleetUpdates() {
  const machines = useMachinesStore();
  const desktop = useDesktopUpdates();
  const bridge = getDesktopBridge();
  const preferences = usePreferencesStore();
  /** The rule chosen for every machine (null until the user chooses one), and the machines that follow their own. */
  const chosenRule = computed(() => {
    const value = preferences.get(UPDATE_RULE_DEFAULT_KEY, "");
    return isUpdateRule(value) ? value : null;
  });
  const ruleOverrides = computed(() => parseRuleOverrides(preferences.get(UPDATE_RULE_OVERRIDES_KEY, "{}")));
  const enabled = useFleetUpdatesEnabled();
  const isChecking = shallowRef(false);
  /** The rollout the home Fleet is running, or its last; the Fleet pushes each change. */
  const rollout = shallowRef<Rollout | null>(null);
  const actionError = shallowRef<string | null>(null);
  /** Set from the moment this Fleet is asked to restart until it answers again. */
  const restartingSelf = shallowRef<{ from: string | null; to: string | null } | null>(null);
  let rolloutTimer: ReturnType<typeof setInterval> | null = null;
  let unsubscribeRollout: (() => void) | null = null;
  /** The version each machine last reported to this device: what an unreachable machine was last seen on. */
  const lastSeen = shallowRef(readLastSeenVersions());

  function machineRow(entry: MachineEntry): FleetUpdateRow {
    const info = entry.info ?? null;
    const reachable = entry.isLive ? entry.isHome || machines.liveReachable : !machines.others[entry.key]?.error;
    const version = info?.version ?? (reachable ? null : lastSeen.value[entry.key] ?? null);
    const state = reachable && !info
      ? { kind: "checking" as const, label: "checking…", tone: "muted" as const, target: null }
      : describeMachineUpdate({
        version,
        update: info?.update ?? null,
        working: busySessions(entry.capabilities?.sessions),
        reachable,
      });
    const step = rollout.value?.status === "running" ? rollout.value.steps.find((candidate) => candidate.machineId === entry.key) : undefined;
    const shown = entry.isHome && restartingSelf.value
      ? { kind: "restarting" as const, label: "restarting · this page reconnects when it's back", tone: "info" as const, target: restartingSelf.value.to }
      // A machine that can't be reached says so, even while a rollout waits to get to it (it will skip it).
      : step && state.kind !== "unreachable" ? describeRolloutStep(step) : state;
    const kind = info?.webApp === false ? "node" : "fleet";
    const overridden = ruleOverrides.value[entry.key] ?? null;
    return {
      key: entry.key,
      name: entry.name,
      kind,
      // The home Fleet reports its rule; a Fleet older than the rule can't take one.
      rule: info?.update?.rule === undefined ? null : { effective: effectiveRule(overridden, chosenRule.value, kind), overridden },
      os: osLabel(entry.os),
      version,
      latest: info?.update?.latestVersion ?? null,
      where: entry.isHome ? "this machine" : entry.baseUrl,
      isLive: entry.isLive,
      state: shown,
      restart: step || restartingSelf.value ? null : restartOffer(state, info?.update ?? null, entry.isHome),
      isHome: entry.isHome,
      working: busySessions(entry.capabilities?.sessions),
    };
  }

  const rows = computed<FleetUpdateRow[]>(() => {
    const list: FleetUpdateRow[] = [];
    if (bridge) {
      list.push({
        key: "desktop",
        name: "This app",
        kind: "app",
        os: osLabel(DESKTOP_PLATFORMS[bridge.platform] ?? bridge.platform),
        version: desktop.state.value?.currentVersion ?? bridge.version,
        latest: desktop.state.value?.version ?? null,
        where: "desktop app",
        isLive: false,
        state: describeDesktopUpdate(desktop.state.value),
        restart: null,
        isHome: false,
        rule: null,
      });
    }
    return [...list, ...machines.entries.map(machineRow)];
  });

  /** The machines alone: the desktop app's row belongs to System. */
  const machineRows = computed(() => rows.value.filter((row) => row.kind !== "app"));
  const summary = computed(() => summarizeRows(machineRows.value));
  const checkedAt = computed(() => newestCheckedAt(machines.entries.map((entry) => entry.info?.update?.checkedAt)));
  const rolloutRunning = computed(() => rollout.value?.status === "running");
  const outcome = computed(() => describeRolloutOutcome(rollout.value));
  /** Machines (not this one) that have an update downloaded and can restart into it: what Update machines now would do. */
  const readyMachines = computed(() => rows.value.filter((row) => !row.isHome && row.restart?.kind === "button").length);
  const isBusy = computed(() => isChecking.value || rows.value.some((row) => row.state.kind === "downloading" || row.state.kind === "checking"));

  /** Asks every reachable Fleet to check for a release now (a node that finds one downloads it), then reads them again. */
  async function checkNow(): Promise<void> {
    isChecking.value = true;
    const refresh = setInterval(() => void machines.refreshUpdates(), BUSY_REFRESH_MS);
    try {
      const checks = machines.entries
        .filter((entry) => entry.info && (entry.isLive ? entry.isHome || machines.liveReachable : !machines.others[entry.key]?.error))
        .map((entry) => fetchOnMachine(entry.connection, "/api/update/check", { method: "POST" }));
      const desktopState = desktop.state.value?.status;
      if (desktopState === "idle" || desktopState === "error") checks.push(desktop.check().then(() => new Response()));
      await Promise.allSettled(checks);
      await machines.refreshUpdates();
    } finally {
      clearInterval(refresh);
      isChecking.value = false;
    }
  }

  async function loadRollout(): Promise<void> {
    try {
      const response = await fetchOnMachine(null, "/api/update/rollout");
      if (response.status === 200) rollout.value = (await response.json()) as Rollout;
    } catch {
      // The next poll or push tries again.
    }
  }

  function stopFollowingRollout(): void {
    if (rolloutTimer) clearInterval(rolloutTimer);
    rolloutTimer = null;
    unsubscribeRollout?.();
    unsubscribeRollout = null;
  }

  /** Reads the rollout and follows it, but only with the switch on: with it off this asks the Fleet for nothing. */
  function followRollout(): void {
    if (rolloutTimer || !enabled.value) return;
    void loadRollout();
    unsubscribeRollout = onDomainEvent(liveTarget(), "sessions", "update.rollout", (event) => {
      rollout.value = event.payload;
    });
    rolloutTimer = setInterval(() => {
      if (rolloutRunning.value) {
        void loadRollout();
        void machines.refreshUpdates();
      }
    }, ROLLOUT_POLL_MS);
  }

  /** Reads a refusal's words from the Fleet (`{ error }`), or says what happened. */
  async function postOnHome(path: string, body?: unknown): Promise<Response | null> {
    actionError.value = null;
    try {
      const response = await fetchOnMachine(null, path, {
        method: "POST",
        headers: body ? { "Content-Type": "application/json" } : undefined,
        body: body ? JSON.stringify(body) : undefined,
      });
      if (!response.ok) {
        const text = (await response.json().catch(() => null)) as { error?: string } | null;
        actionError.value = text?.error ?? `The Fleet answered ${response.status}.`;
        return null;
      }
      return response;
    } catch {
      actionError.value = "Couldn't reach this Fleet.";
      return null;
    }
  }

  /** Restarts the machines (all that can, or just `machineIds`) one at a time, from the home Fleet. */
  async function startRollout(machineIds: string[] = []): Promise<void> {
    const response = await postOnHome("/api/update/rollout", { machineIds });
    if (response) rollout.value = (await response.json()) as Rollout;
  }

  async function stopRollout(): Promise<void> {
    await fetchOnMachine(null, "/api/update/rollout", { method: "DELETE" }).catch(() => undefined);
  }

  /** Restarts this Fleet, then waits for it to answer again and reloads the page onto what it came back as. */
  async function restartSelf(row: FleetUpdateRow): Promise<void> {
    const response = await postOnHome("/api/update/apply");
    if (!response) return;
    restartingSelf.value = { from: row.version, to: row.state.target };
    const started = Date.now();
    let wentDown = false;
    const poll = setInterval(async () => {
      let answered = false;
      try {
        answered = (await fetchOnMachine(null, "/api/machine")).ok;
      } catch {
        answered = false;
      }
      if (!answered) wentDown = true;
      if (answered && wentDown) {
        clearInterval(poll);
        window.location.reload();
      } else if (Date.now() - started > COME_BACK_LIMIT_MS) {
        clearInterval(poll);
        restartingSelf.value = null;
        actionError.value = "This Fleet didn't come back within 3 minutes.";
      }
    }, COME_BACK_POLL_MS);
  }

  /** The button on a row: this machine restarts itself; any other is a rollout of that one machine. */
  function restart(row: FleetUpdateRow): Promise<void> {
    return row.key === HOME_MACHINE_KEY ? restartSelf(row) : startRollout([row.key]);
  }

  /** Chooses the rule for every machine that has no rule of its own (null: none chosen, each kind follows its own default); the home Fleet gives it to them within a minute. */
  async function chooseRule(rule: UpdateRule | null): Promise<void> {
    await preferences.set(UPDATE_RULE_DEFAULT_KEY, rule ?? "");
  }

  /** Gives one machine its own rule, or (null) puts it back on the chosen one. */
  async function overrideRule(key: string, rule: UpdateRule | null): Promise<void> {
    const next = { ...ruleOverrides.value };
    if (rule) next[key] = rule;
    else delete next[key];
    await preferences.set(UPDATE_RULE_OVERRIDES_KEY, JSON.stringify(next));
  }

  onMounted(followRollout);
  watch(enabled, (on) => (on ? followRollout() : stopFollowingRollout()));
  watch(
    () => (enabled.value ? machines.entries.map((entry) => [entry.key, entry.info?.version] as const) : []),
    (reported) => {
      const next = { ...lastSeen.value };
      for (const [key, version] of reported) if (version) next[key] = version;
      if (reported.some(([key, version]) => version && lastSeen.value[key] !== version)) {
        lastSeen.value = next;
        writeLastSeenVersions(next);
      }
    },
    { immediate: true },
  );
  onUnmounted(stopFollowingRollout);

  return {
    enabled,
    rows,
    machineRows,
    summary,
    checkedAt,
    isBusy,
    isChecking,
    checkNow,
    openDownload: desktop.install,
    desktopInApp: desktop.inApp,
    rollout,
    rolloutRunning,
    outcome,
    readyMachines,
    actionError,
    restartingSelf,
    startRollout,
    stopRollout,
    restart,
    chosenRule,
    chooseRule,
    overrideRule,
  };
}

/**
 * Follows the machines while Settings or the status bar chip needs them, and says how many machines have an update
 * waiting (the desktop app's own row isn't one). Only does anything while the switch is on.
 */
export function useFleetUpdatesWatch() {
  const enabled = useFleetUpdatesEnabled();
  const machines = useMachinesStore();
  const { rows, rollout } = useFleetUpdates();
  const waiting = computed(() => (enabled.value ? rows.value.filter((row) => row.kind !== "app" && hasUpdateWaiting(row.state)).length : 0));
  let stop: (() => void) | null = null;

  function sync(): void {
    if (enabled.value && !stop) stop = machines.startPolling();
    else if (!enabled.value && stop) {
      stop();
      stop = null;
    }
  }

  onMounted(sync);
  watch(enabled, sync);
  onUnmounted(() => stop?.());
  return { enabled, waiting, rows, rollout };
}
