import { computed, readonly, shallowRef } from "vue";
import { apiFetch, apiFetchOn } from "@/lib/api-client";
import { onDomainEvent } from "@/composables/on-domain-event";
import { onReconnect } from "@/composables/use-weave-socket";
import { liveTarget, type MachineTarget } from "@/lib/machine-target";
import { toUsageLimits, USAGE_LIMITS_EVENT, type ProviderUsage, type UsageLimits } from "@/lib/usage-providers";

// Every provider's usage limits on the live machine, and (while the popover is open) on the other connected ones: one copy for the page, however many chips and popovers read it.
const limits = shallowRef<UsageLimits>({ enabled: false, providers: [] });
// Other machines' providers by machine key. Asked only while the popover is open (see `watch`), never pushed.
const otherProviders = shallowRef<Record<string, ProviderUsage[]>>({});
let otherTargets: (() => readonly MachineTarget[]) | null = null;
let started = false;
let loadId = 0;
let watchers = 0;
let watchTimer: ReturnType<typeof setInterval> | null = null;

const WATCH_INTERVAL_MS = 60_000;

async function load(watch = false): Promise<void> {
  const current = ++loadId;
  try {
    const response = await apiFetch(`/api/usage/limits${watch ? "?watch=true" : ""}`);
    if (!response.ok) return;
    const next = toUsageLimits(await response.json());
    if (next && current === loadId) limits.value = next;
  } catch (error) {
    console.warn("Failed to load the usage limits:", error);
  }
}

/** Asks each other connected machine for its usage; one that doesn't answer keeps its last answer. */
async function loadOthers(): Promise<void> {
  const targets = (otherTargets?.() ?? []).filter((target) => !target.isLive);
  await Promise.all(targets.map(async (target) => {
    try {
      const response = await apiFetchOn(target.connection, "/api/usage/limits?watch=true");
      if (!response.ok) return;
      const next = toUsageLimits(await response.json());
      if (next) otherProviders.value = { ...otherProviders.value, [target.key]: next.providers };
    } catch (error) {
      console.warn(`Failed to load the usage limits of ${target.key}:`, error);
    }
  }));
}

function start(): void {
  if (started) return;
  started = true;
  onDomainEvent(liveTarget(), "sessions", USAGE_LIMITS_EVENT, (event) => {
    const next = toUsageLimits(event.payload);
    if (!next) return;
    // Newer than any load in flight.
    loadId += 1;
    limits.value = next;
  });
  onReconnect(liveTarget(), () => void load());
  void load();
}

async function setAllowed(provider: string, allowed: boolean): Promise<void> {
  const response = await apiFetch(`/api/usage/limits/${encodeURIComponent(provider)}/allow`, {
    method: allowed ? "POST" : "DELETE",
  });
  if (!response.ok) throw new Error(`Could not ${allowed ? "allow" : "revoke"} ${provider}: ${response.status}`);
  await load();
}

/**
 * While something shows the popover, asks the server to refresh stale providers now and once a minute. Returns what
 * stops it; several watchers share one timer.
 */
function watch(others?: () => readonly MachineTarget[]): () => void {
  watchers += 1;
  if (others) otherTargets = others;
  if (watchers === 1) {
    const refresh = () => {
      void load(true);
      void loadOthers();
    };
    refresh();
    watchTimer = setInterval(refresh, WATCH_INTERVAL_MS);
  }
  let stopped = false;
  return () => {
    if (stopped) return;
    stopped = true;
    watchers -= 1;
    if (watchers === 0 && watchTimer) {
      clearInterval(watchTimer);
      watchTimer = null;
      otherTargets = null;
    }
  };
}

export function useUsageLimits() {
  start();
  return {
    limits: readonly(limits),
    providers: computed(() => limits.value.providers),
    otherProviders: computed(() => otherProviders.value),
    reload: load,
    watch,
    allow: (provider: string) => setAllowed(provider, true),
    revoke: (provider: string) => setAllowed(provider, false),
  };
}

/** For tests: forget what was loaded, and load again on next use. */
export function resetUsageLimitsForTests(): void {
  limits.value = { enabled: false, providers: [] };
  otherProviders.value = {};
  otherTargets = null;
  started = false;
  loadId = 0;
  watchers = 0;
  if (watchTimer) clearInterval(watchTimer);
  watchTimer = null;
}
