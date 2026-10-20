<script setup lang="ts">
import { computed, onBeforeUnmount, shallowRef, watch } from "vue";
import { Gauge } from "lucide-vue-next";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { useUsageLimits } from "@/composables/use-usage-limits";
import { resetLabel } from "@/lib/usage-limits";
import {
  providerWindowLabel,
  providerWindows,
  providerWindowShortLabel,
  tightestWindow,
  usageTone,
  windowPercent,
  type ProviderUsage,
} from "@/lib/usage-providers";

/**
 * The usage limit in the status bar, grown into a popover: the fullest window of any provider Fleet can read, quiet
 * grey until 80%, amber from there and red when used up. Click for every provider: bars, reset times, where each
 * sign-in was read from, and an Allow button for the ones Fleet hasn't been told it may read.
 */
const { providers, watch: watchLimits, allow } = useUsageLimits();

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
  stopWatching = isOpen ? watchLimits() : null;
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
  <Popover
    v-if="chip"
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
      <section
        v-for="provider in providers"
        :key="`${provider.provider}:${provider.account ?? ''}`"
        class="usage-pop__provider"
        :data-testid="`usage-provider-${provider.provider}`"
      >
        <h4>
          <span>{{ provider.name }}</span>
          <span v-if="provider.account">{{ provider.account }}</span>
        </h4>
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
            ><i :style="{ width: `${windowPercent(window)}%` }" /></span>
            <span class="usage-pop__percent">{{ windowPercent(window) }}%</span>
            <span class="usage-pop__reset">{{ resetLabel(window.resetsAt, now) ?? "" }}</span>
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
          <button
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
      <p
        v-if="providers.length === 0"
        class="usage-pop__note"
      >
        No provider sign-ins found.
      </p>
    </PopoverContent>
  </Popover>
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
  max-height: 430px;
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
  height: 6px;
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
