<script setup lang="ts">
import { computed, onBeforeUnmount, shallowRef, watch } from "vue";
import { Gauge } from "lucide-vue-next";
import { createReusableTemplate } from "@vueuse/core";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Sheet, SheetContent, SheetDescription, SheetTitle } from "@/components/ui/sheet";
import { useMediaQuery } from "@/composables/use-media-query";
import { useUsageLimits } from "@/composables/use-usage-limits";
import { targetFor } from "@/lib/machine-target";
import { useMachinesStore } from "@/stores/machines";
import { resetLabel, type UsageLimitWindow } from "@/lib/usage-limits";
import {
  expectedShare,
  groupUsageByMachine,
  paceNote,
  providerWindowLabel,
  providerWindows,
  providerWindowShortLabel,
  tightestWindow,
  usageTone,
  windowPercent,
  type MachineProviders,
  type ProviderUsage,
} from "@/lib/usage-providers";

/**
 * The usage limit in the status bar, grown into a popover: the fullest window of any provider Fleet can read, quiet
 * grey until 80%, amber from there and red when used up. Click for every provider: bars, reset times, where each
 * sign-in was read from, and an Allow button for the ones Fleet hasn't been told it may read.
 */
const { providers, otherProviders, watch: watchLimits, allow } = useUsageLimits();
const machines = useMachinesStore();

// The chip and the details are each drawn once and placed in a popover, or in a bottom sheet on a phone.
const [DefineChip, ReuseChip] = createReusableTemplate();
const [DefineBody, ReuseBody] = createReusableTemplate();
const isPhone = useMediaQuery("(max-width: 600px)");

/** Machines other than the live one that answered their last check: only these are asked, and only while open. */
const connectedOthers = computed(() => machines.entries.filter((entry) => {
  const state = machines.others[entry.key];
  return !entry.isLive && state !== undefined && state.loadedAt !== null && !state.error;
}));

// The live machine's data first and primary; the others as they last answered.
const groups = computed(() => {
  const live = machines.live;
  const list: MachineProviders[] = [{ key: liveKey.value, name: live?.name ?? "This machine", providers: providers.value }];
  for (const entry of connectedOthers.value) list.push({ key: entry.key, name: entry.name, providers: otherProviders.value[entry.key] ?? [] });
  return groupUsageByMachine(list);
});
const liveKey = computed(() => machines.live?.key ?? "live");
const showMachines = computed(() => groups.value.length >= 2);

// Once a minute: a window that resets drops off without waiting for the server to say so.
const now = shallowRef(Date.now());
const timer = setInterval(() => (now.value = Date.now()), 60_000);
onBeforeUnmount(() => clearInterval(timer));

const open = shallowRef(false);
// While open, the server refreshes any provider it last asked more than a minute ago.
let stopWatching: (() => void) | null = null;
watch(open, (isOpen) => {
  now.value = Date.now();
  stopWatching?.();
  stopWatching = isOpen ? watchLimits(() => connectedOthers.value.map((entry) => targetFor(entry.connection))) : null;
});
onBeforeUnmount(() => stopWatching?.());

const tightest = computed(() => tightestWindow(providers.value, now.value));
const chip = computed(() => {
  const found = tightest.value;
  // Nothing readable yet (a provider waiting for Allow, say): the chip is still there to open.
  if (!found) {
    return providers.value.length === 0
      ? null
      : { name: "Usage", percent: null, tone: "quiet" as const, label: "Usage", title: "Usage limits" };
  }
  const percent = windowPercent(found.window);
  const reset = resetLabel(found.window.resetsAt, now.value);
  const name = `${found.provider.name} ${providerWindowShortLabel(found.window.window)}`;
  return {
    name,
    percent,
    tone: usageTone(found.window),
    label: `${name} ${percent}%`,
    title: `${found.provider.name}'s ${providerWindowLabel(found.window.window).toLowerCase()} is ${percent}% used${reset ? `; it ${reset}` : ""}.`,
  };
});

const clock = (at: number) => new Date(at).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });

function readLine(provider: ProviderUsage): string | null {
  const parts: string[] = [];
  if (provider.source) parts.push(`read from ${provider.source}`);
  if (provider.readAt !== null) parts.push(`last asked ${clock(provider.readAt)}`);
  return parts.length ? parts.join(", ") : null;
}

function tickPercent(window: UsageLimitWindow): number | null {
  const share = expectedShare(window, now.value);
  return share === null ? null : Math.round(share * 100);
}

const credits = (provider: ProviderUsage) => {
  const c = provider.credits;
  if (!c) return null;
  const dollars = (value: number) => `$${value.toFixed(2)}`;
  if (c.remaining !== null) return `${dollars(c.remaining)} left${c.limit !== null ? ` of ${dollars(c.limit)}` : ""}`;
  return c.used !== null ? `${dollars(c.used)} used` : null;
};

const allowing = shallowRef<string | null>(null);
async function onAllow(provider: string): Promise<void> {
  allowing.value = provider;
  try {
    await allow(provider);
  } catch (error) {
    console.warn("Failed to allow reading usage:", error);
  } finally {
    allowing.value = null;
  }
}
</script>

<template>
  <DefineChip>
    <template v-if="chip">
      <Gauge
        class="usage-chip__icon"
        aria-hidden="true"
      />
      {{ chip.name }}
      <template v-if="chip.percent !== null">
        <span
          class="usage-chip__bar"
          aria-hidden="true"
        ><i :style="{ width: `${chip.percent}%` }" /></span>
        <span>{{ chip.percent }}%</span>
      </template>
    </template>
  </DefineChip>

  <DefineBody>
    <template
      v-for="group in groups"
      :key="group.key"
    >
      <h3
        v-if="showMachines"
        class="usage-pop__machine"
        data-testid="usage-machine"
      >
        {{ group.name }}
      </h3>
      <p
        v-if="showMachines && group.rows.length === 0"
        class="usage-pop__note"
      >
        No provider sign-ins found.
      </p>
      <section
        v-for="{ provider, alsoOn } in group.rows"
        :key="`${provider.provider}:${provider.account ?? ''}`"
        class="usage-pop__provider"
        :data-testid="`usage-provider-${provider.provider}`"
      >
        <h4>
          <span>{{ provider.name }}</span>
          <span v-if="provider.account">{{ provider.account }}</span>
        </h4>
        <p
          v-if="alsoOn.length"
          class="usage-pop__note"
          data-testid="usage-also-on"
        >
          also on {{ alsoOn.join(", ") }}
        </p>
        <p
          v-if="readLine(provider)"
          class="usage-pop__note"
        >
          {{ readLine(provider) }}
        </p>

        <template v-if="provider.state === 'ok'">
          <div
            v-for="window in providerWindows(provider, now)"
            :key="window.window"
            class="usage-pop__row"
            :class="`usage-pop__row--${usageTone(window)}`"
          >
            <span>{{ providerWindowLabel(window.window) }}</span>
            <span
              class="usage-pop__bar"
              role="progressbar"
              :aria-valuenow="windowPercent(window)"
              aria-valuemin="0"
              aria-valuemax="100"
            >
              <i :style="{ width: `${windowPercent(window)}%` }" />
              <b
                v-if="tickPercent(window) !== null"
                class="usage-pop__tick"
                :style="{ left: `${tickPercent(window)}%` }"
                :title="`An even share of this window by now: ${tickPercent(window)}%`"
                data-testid="usage-pace-tick"
              />
            </span>
            <span class="usage-pop__percent">{{ windowPercent(window) }}%</span>
            <span class="usage-pop__reset">{{ resetLabel(window.resetsAt, now) ?? "" }}</span>
            <p
              v-if="paceNote(window, now)"
              class="usage-pop__pace"
              data-testid="usage-pace-note"
            >
              {{ paceNote(window, now)?.text }}
            </p>
          </div>
          <p
            v-if="credits(provider)"
            class="usage-pop__credits"
          >
            Credits: {{ credits(provider) }}
          </p>
        </template>

        <template v-else-if="provider.state === 'not_allowed'">
          <p class="usage-pop__note">
            Fleet found a sign-in{{ provider.source ? ` in ${provider.source}` : "" }}. Allow it to ask {{ provider.name }}
            how much you've used? It reads the sign-in read-only, never saves it, and sends it nowhere but {{ provider.name }}.
          </p>
          <p
            v-if="group.key !== liveKey"
            class="usage-pop__note"
          >
            Open {{ group.name }} to allow it there.
          </p>
          <button
            v-else
            type="button"
            class="usage-pop__allow"
            :disabled="allowing === provider.provider"
            :data-testid="`usage-allow-${provider.provider}`"
            @click="onAllow(provider.provider)"
          >
            Allow
          </button>
        </template>
        <p
          v-else-if="provider.state === 'no_window'"
          class="usage-pop__note"
        >
          Pay-per-token: no limits to show.
        </p>
        <p
          v-else-if="provider.state === 'signed_out'"
          class="usage-pop__note"
        >
          Sign in again{{ provider.source ? ` in ${provider.source}` : "" }}.
        </p>
        <p
          v-else-if="provider.state === 'unsupported'"
          class="usage-pop__note"
        >
          Fleet can't read this sign-in yet.
        </p>
        <p
          v-else
          class="usage-pop__note"
        >
          Can't read this right now.
        </p>
      </section>
    </template>
    <p
      v-if="!showMachines && providers.length === 0"
      class="usage-pop__note"
    >
      No provider sign-ins found.
    </p>
  </DefineBody>

  <template v-if="chip">
    <Sheet
      v-if="isPhone"
      v-model:open="open"
    >
      <button
        type="button"
        class="usage-chip"
        :class="`usage-chip--${chip.tone}`"
        :title="chip.title"
        :aria-label="chip.title"
        data-testid="usage-limits-chip"
        @click="open = !open"
      >
        <ReuseChip />
      </button>
      <SheetContent
        side="bottom"
        class="usage-sheet max-h-[calc(var(--visual-vh,100dvh)*0.8)] overflow-y-auto px-4 pb-[env(safe-area-inset-bottom,0px)]"
        data-testid="usage-limits-sheet"
      >
        <SheetTitle>Usage limits</SheetTitle>
        <SheetDescription class="sr-only">
          How much of each provider's limits you've used.
        </SheetDescription>
        <div class="usage-pop usage-pop--sheet">
          <ReuseBody />
        </div>
      </SheetContent>
    </Sheet>
    <Popover
      v-else
      v-model:open="open"
    >
      <PopoverTrigger as-child>
        <button
          type="button"
          class="usage-chip"
          :class="`usage-chip--${chip.tone}`"
          :title="chip.title"
          :aria-label="chip.title"
          data-testid="usage-limits-chip"
        >
          <ReuseChip />
        </button>
      </PopoverTrigger>
      <PopoverContent
        side="top"
        align="end"
        :side-offset="8"
        :collision-padding="8"
        class="usage-pop"
        data-testid="usage-limits-popover"
      >
        <ReuseBody />
      </PopoverContent>
    </Popover>
  </template>
</template>

<style scoped>
.usage-chip {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  gap: 6px;
  height: 18px;
  padding: 0 8px 0 7px;
  border-radius: 999px;
  background: transparent;
  color: var(--muted);
  font-size: 11px;
  font-weight: 500;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
  cursor: pointer;
}

.usage-chip--warn {
  background: color-mix(in srgb, var(--status-waiting) 14%, transparent);
  color: var(--status-waiting);
}

.usage-chip--used {
  background: color-mix(in srgb, var(--error) 14%, transparent);
  color: var(--error);
}

.usage-chip__icon {
  width: 12px;
  height: 12px;
}

.usage-chip__bar,
.usage-pop__bar {
  display: inline-block;
  overflow: hidden;
  border-radius: 999px;
  background: color-mix(in srgb, currentColor 22%, transparent);
}

.usage-chip__bar {
  width: 30px;
  height: 4px;
}

.usage-chip__bar i,
.usage-pop__bar i {
  display: block;
  height: 100%;
  border-radius: inherit;
  background: currentColor;
}

/* A phone's status bar has room for the window and the number, not the bar. */
@media (max-width: 600px) {
  .usage-chip__bar {
    display: none;
  }
}
</style>

<!-- The popover renders in a portal, outside this component's scope. -->
<style>
.usage-pop {
  width: 380px;
  max-width: calc(100vw - 16px);
  max-height: min(560px, var(--reka-popover-content-available-height, 560px));
  overflow: auto;
  padding: 12px 14px;
  border-radius: var(--radius-panel);
  background: var(--card-bg);
  box-shadow: var(--menu-shadow);
  font-size: 12px;
}

.usage-pop__provider + .usage-pop__provider {
  margin-top: 12px;
  padding-top: 12px;
  border-top: 1px solid var(--border);
}

.usage-pop h4 {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  margin: 0 0 2px;
  color: var(--text);
  font-size: 12px;
  font-weight: 600;
}

.usage-pop h4 span + span {
  color: var(--muted);
  font-weight: 400;
}

.usage-pop__note {
  margin: 2px 0 6px;
  color: var(--muted);
}

.usage-pop__row {
  display: grid;
  grid-template-columns: minmax(70px, 1fr) 90px 36px 112px;
  align-items: center;
  gap: 8px;
  padding: 3px 0;
  color: var(--accent);
  font-variant-numeric: tabular-nums;
}

.usage-pop__row > :first-child {
  color: var(--text);
}

.usage-pop__row--warn {
  color: var(--status-waiting);
}

.usage-pop__row--used {
  color: var(--error);
}

.usage-pop__bar {
  position: relative;
  height: 6px;
}

/* A thin mark at an even share of the window; the ring keeps it visible on both the filled and unfilled parts. */
.usage-pop__tick {
  position: absolute;
  top: 0;
  bottom: 0;
  width: 2px;
  margin-left: -1px;
  background: #fff;
  box-shadow: 0 0 0 1px color-mix(in srgb, #000 45%, transparent);
}

.usage-pop__pace {
  grid-column: 1 / -1;
  margin: 0;
  color: var(--status-waiting);
  font-size: 11px;
}

.usage-pop__percent {
  text-align: right;
}

.usage-pop__reset {
  white-space: nowrap;
  color: var(--muted);
  text-align: right;
}

.usage-pop__credits {
  margin: 4px 0 0;
  color: var(--text);
}

.usage-pop__allow {
  padding: 3px 12px;
  border-radius: 6px;
  background: var(--accent);
  color: #fff;
  font-size: 12px;
  font-weight: 500;
}

.usage-pop__allow:disabled {
  opacity: 0.6;
}

/* On a phone the same list sits in a bottom sheet, which brings its own width and shadow. */
.usage-pop--sheet {
  width: auto;
  max-width: none;
  max-height: none;
  padding: 0 0 12px;
  background: transparent;
  box-shadow: none;
}

.usage-pop__machine {
  margin: 0 0 6px;
  color: var(--muted);
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.06em;
  text-transform: uppercase;
}

.usage-pop__machine:not(:first-child) {
  margin-top: 14px;
}

@media (max-width: 600px) {
  .usage-pop__row {
    grid-template-columns: 1fr 60px 34px;
  }

  .usage-pop__reset {
    grid-column: 1 / -1;
    text-align: left;
  }
}
</style>
