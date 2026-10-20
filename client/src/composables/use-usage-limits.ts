import { computed, readonly, shallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";
import { onDomainEvent } from "@/composables/on-domain-event";
import { onReconnect } from "@/composables/use-weave-socket";
import { liveTarget } from "@/lib/machine-target";
import { toUsageLimits, USAGE_LIMITS_EVENT, type UsageLimits } from "@/lib/usage-providers";

// Every provider's usage limits: one copy for the page, however many chips and popovers read it.
const limits = shallowRef<UsageLimits>({ enabled: false, providers: [] });
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
function watch(): () => void {
  watchers += 1;
  if (watchers === 1) {
    void load(true);
    watchTimer = setInterval(() => void load(true), WATCH_INTERVAL_MS);
  }
  let stopped = false;
  return () => {
    if (stopped) return;
    stopped = true;
    watchers -= 1;
    if (watchers === 0 && watchTimer) {
      clearInterval(watchTimer);
      watchTimer = null;
    }
  };
}

export function useUsageLimits() {
  start();
  return {
    limits: readonly(limits),
    providers: computed(() => limits.value.providers),
    reload: load,
    watch,
    allow: (provider: string) => setAllowed(provider, true),
    revoke: (provider: string) => setAllowed(provider, false),
  };
}

/** For tests: forget what was loaded, and load again on next use. */
export function resetUsageLimitsForTests(): void {
  limits.value = { enabled: false, providers: [] };
  started = false;
  loadId = 0;
  watchers = 0;
  if (watchTimer) clearInterval(watchTimer);
  watchTimer = null;
}
