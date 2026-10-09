import { computed, reactive, readonly, toValue, type MaybeRefOrGetter } from "vue";
import { api } from "@/api/client";
import { onDomainEvent } from "@/composables/on-domain-event";
import { onReconnect } from "@/composables/use-weave-socket";
import { liveTarget } from "@/lib/machine-target";
import { HARNESS_USAGE_EVENT, toHarnessUsage, type HarnessUsage } from "@/lib/usage-limits";

// The user's harnesses' usage limits, by harness type: one copy for the page, however many cards and chips read it.
const usageByHarness = reactive<Record<string, HarnessUsage>>({});
let started = false;
let loadId = 0;

async function load(): Promise<void> {
  const current = ++loadId;
  try {
    const { data, response } = await api.GET("/api/harnesses/usage");
    if (current !== loadId || !response.ok || !Array.isArray(data)) return;
    for (const key of Object.keys(usageByHarness)) delete usageByHarness[key];
    for (const raw of data) {
      const usage = toHarnessUsage(raw);
      if (usage) usageByHarness[usage.harnessType] = usage;
    }
  } catch (error) {
    console.warn("Failed to load the harnesses' usage limits:", error);
  }
}

/** Loads the limits once and follows the pushes for the rest of the page's life, reloading after a reconnect. */
function start(): void {
  if (started) return;
  started = true;
  onDomainEvent(liveTarget(), "sessions", HARNESS_USAGE_EVENT, (event) => {
    const usage = toHarnessUsage(event.payload);
    if (!usage) return;
    // Newer than any load in flight.
    loadId += 1;
    usageByHarness[usage.harnessType] = usage;
  });
  onReconnect(liveTarget(), () => void load());
  void load();
}

/** Every harness's usage limits the user has; empty when none reports any (an API key, a gateway). */
export function useHarnessUsage() {
  start();
  return { usage: readonly(usageByHarness) as Readonly<Record<string, HarnessUsage>> };
}

/** One harness's usage limits, or null when it hasn't reported any. */
export function useHarnessUsageFor(harnessType: MaybeRefOrGetter<string | null | undefined>) {
  const { usage } = useHarnessUsage();
  return computed<HarnessUsage | null>(() => {
    const type = toValue(harnessType);
    return type ? (usage[type] as HarnessUsage | undefined) ?? null : null;
  });
}

/** For tests: forget what was loaded, and load again on next use. */
export function resetHarnessUsageForTests(): void {
  for (const key of Object.keys(usageByHarness)) delete usageByHarness[key];
  started = false;
  loadId = 0;
}
