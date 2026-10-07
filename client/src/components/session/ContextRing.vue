<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { storeToRefs } from "pinia";
import { LoaderCircle } from "lucide-vue-next";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { useSessionContext } from "@/composables/use-session-context";
import {
  CONTEXT_WARN_PERCENT,
  contextPercent,
  contextTone,
  formatTokens,
  turnsBeforeCompaction,
  type ContextTurn,
} from "@/lib/context-usage";
import { useSessionsStore } from "@/stores/sessions";
import { useHarnessUsageFor } from "@/composables/use-harness-usage";
import { currentWindows, isNearLimit, percentLabel, resetLabel, windowLabel } from "@/lib/usage-limits";

/**
 * How full the session's context window is, by Send: a ring, with the percentage once it's three-quarters full. Click
 * it for the details (the size against the model's window, each turn's size, the last call's tokens) and Compact now.
 * Nothing shows until the session's harness has reported a model call.
 */
const props = defineProps<{
  sessionId: string;
}>();

const { context, isRequesting, requestError, compact } = useSessionContext(() => props.sessionId);
const { sessions } = storeToRefs(useSessionsStore());
const session = computed(() => sessions.value.find((item) => item.session.id === props.sessionId) ?? null);

// The account's usage limits under the context, when the session's harness reports them (Claude Code on a claude.ai
// login); nothing for an API key or a gateway.
const harnessUsage = useHarnessUsageFor(() => session.value?.harnessType);
const limits = computed(() => {
  const now = Date.now();
  return currentWindows(harnessUsage.value, now).map((window) => ({
    key: window.window,
    label: windowLabel(window.window),
    value: [percentLabel(window), resetLabel(window.resetsAt, now)].filter(Boolean).join(" · "),
    fill: window.status === "rejected" ? 100 : Math.round((window.utilization ?? 0) * 100),
    tone: window.status === "rejected" ? "danger" : isNearLimit(window) ? "warn" : "ok",
  }));
});

const open = shallowRef(false);
const hoveredTurn = shallowRef<number | null>(null);

const percent = computed(() => contextPercent(context.value));
const tone = computed(() => contextTone(percent.value));
const showPercent = computed(() => percent.value !== null && percent.value >= CONTEXT_WARN_PERCENT);
const compacting = computed(() => context.value?.compacting === true || isRequesting.value);

/** The ring's arc, 0–1. An unknown size (right after a compaction) draws an empty ring. */
const fill = computed(() => {
  const value = context.value;
  if (!value || value.used === null) return 0;
  if (value.limit === null) return 1;
  return Math.min(1, value.used / value.limit);
});

const RADIUS = 6;
const CIRCUMFERENCE = 2 * Math.PI * RADIUS;
const dash = computed(() => `${Math.max(0.02, fill.value) * CIRCUMFERENCE} ${CIRCUMFERENCE}`);

const stateLabel = computed(() => {
  const value = context.value;
  if (!value) return "";
  if (compacting.value) return "Compacting…";
  if (value.used === null) return value.compactedAt ? "Compacted" : "Not measured yet";
  if (percent.value === null) return "Window unknown";
  if (tone.value === "danger") return "Nearly full";
  return tone.value === "warn" ? "Getting full" : "Plenty of room";
});

/** What the ring says on hover, and to a screen reader. */
const summary = computed(() => {
  const value = context.value;
  if (!value) return "";
  if (compacting.value) return "Compacting the context…";
  if (value.used === null) return "Context compacted. Its size shows after the next reply.";
  if (percent.value === null) return `${formatTokens(value.used)} tokens in the context. The model's window isn't known.`;
  return `${percent.value}% of the context window used`;
});

const headline = computed(() => {
  const value = context.value;
  if (!value || value.used === null) return "—";
  return percent.value === null ? formatTokens(value.used) : String(percent.value);
});

const sizeLine = computed(() => {
  const value = context.value;
  if (!value) return "";
  const model = value.modelId ? ` · ${value.modelId}` : "";
  if (value.used === null) {
    return value.limit === null ? `Window unknown${model}` : `${value.limit.toLocaleString()} token window${model}`;
  }
  return value.limit === null
    ? `${value.used.toLocaleString()} tokens${model}`
    : `${value.used.toLocaleString()} of ${value.limit.toLocaleString()} tokens${model}`;
});

/** Where the harness compacts, as a share of the window, for the tick on the bar. */
const compactsAtPercent = computed(() => {
  const value = context.value;
  if (!value || value.compactsAt === null || value.limit === null) return null;
  return Math.min(100, (value.compactsAt / value.limit) * 100);
});

/** The turns' bars, scaled to the window, or to the largest turn when the window isn't known. */
const bars = computed(() => {
  const value = context.value;
  if (!value) return [];
  const scale = value.limit ?? Math.max(1, ...value.turns.map((turn) => turn.used));
  return value.turns.map((turn, index) => ({
    turn,
    index,
    height: Math.max(4, Math.min(100, (turn.used / scale) * 100)),
    last: index === value.turns.length - 1,
  }));
});

function describeTurn(turn: ContextTurn, index: number): string {
  return `Turn ${index + 1} · ${formatTokens(turn.used)}${turn.afterCompaction ? " · after a compaction" : ""}`;
}

const chartCaption = computed(() => {
  const value = context.value;
  if (!value) return "";
  if (hoveredTurn.value !== null && value.turns[hoveredTurn.value]) {
    return describeTurn(value.turns[hoveredTurn.value], hoveredTurn.value);
  }
  return `${value.turns.length} ${value.turns.length === 1 ? "turn" : "turns"}`;
});

const turnsLeft = computed(() => (context.value ? turnsBeforeCompaction(context.value) : null));

const cost = computed(() => {
  const total = session.value?.totalCost;
  return typeof total === "number" && total > 0 ? `$${total.toFixed(2)}` : null;
});

const canCompact = computed(() => session.value?.capabilities?.canCompact !== false && !compacting.value);
const compactTitle = computed(() =>
  compacting.value
    ? "The context is being compacted."
    : (session.value?.capabilities?.compactDisabledReason ?? "Summarise the conversation so far, so the agent can keep going."),
);
const compactHint = computed(() =>
  tone.value === "ok" || percent.value === null
    ? "The agent compacts on its own when it gets close."
    : "Compacting summarises earlier turns so the agent can keep going.",
);
const error = computed(() => requestError.value ?? context.value?.compactionError ?? null);
</script>

<template>
  <Popover
    v-if="context"
    v-model:open="open"
  >
    <PopoverTrigger as-child>
      <button
        type="button"
        class="context-ring"
        :class="{ 'context-ring--with-percent': showPercent, 'context-ring--compacting': compacting }"
        :data-tone="tone"
        data-testid="context-ring"
        :aria-label="`${summary}. Details`"
        :title="open ? undefined : `${summary} · details`"
      >
        <svg
          class="context-ring__svg"
          viewBox="0 0 16 16"
          aria-hidden="true"
        >
          <circle
            class="context-ring__track"
            cx="8"
            cy="8"
            :r="RADIUS"
            :stroke-dasharray="context.limit === null && context.used !== null ? '2 2' : undefined"
          />
          <circle
            v-if="context.used !== null && context.limit !== null"
            class="context-ring__fill"
            cx="8"
            cy="8"
            :r="RADIUS"
            :stroke-dasharray="dash"
          />
        </svg>
        <span
          v-if="showPercent"
          class="context-ring__percent"
        >{{ percent }}%</span>
      </button>
    </PopoverTrigger>

    <PopoverContent
      side="top"
      align="end"
      :side-offset="8"
      :collision-padding="8"
      class="context-popover"
      data-testid="context-popover"
    >
      <div
        class="context-card"
        :data-tone="tone"
      >
        <div class="context-card__head">
          <div>
            <p class="context-card__eyebrow">
              Context window
            </p>
            <p class="context-card__big">
              {{ headline }}<small v-if="percent !== null">%</small>
            </p>
          </div>
          <span class="context-card__state">
            <LoaderCircle
              v-if="compacting"
              class="context-card__spin"
              :size="12"
              aria-hidden="true"
            />
            <span
              v-else
              class="context-card__dot"
              aria-hidden="true"
            />
            {{ stateLabel }}
          </span>
        </div>
        <p class="context-card__sub">
          {{ sizeLine }}
        </p>

        <template v-if="context.limit !== null">
          <div
            class="context-card__meter"
            aria-hidden="true"
          >
            <div
              class="context-card__meter-fill"
              :style="{ width: `${percent ?? 0}%` }"
            />
            <div
              v-if="compactsAtPercent !== null"
              class="context-card__meter-tick"
              :style="{ left: `${compactsAtPercent}%` }"
            />
          </div>
          <div class="context-card__legend">
            <span>0</span>
            <span v-if="compactsAtPercent !== null">compacts at ~{{ Math.round(compactsAtPercent) }}%</span>
            <span v-else>{{ formatTokens(context.limit) }}</span>
          </div>
        </template>

        <template v-if="bars.length">
          <p class="context-card__label">
            <span>After each turn</span>
            <span>{{ chartCaption }}</span>
          </p>
          <div
            class="context-card__chart"
            @mouseleave="hoveredTurn = null"
          >
            <div
              v-if="compactsAtPercent !== null"
              class="context-card__chart-limit"
              :style="{ bottom: `${compactsAtPercent}%` }"
            />
            <div
              v-for="bar in bars"
              :key="bar.turn.at"
              class="context-card__bar"
              :class="{ 'context-card__bar--last': bar.last, 'context-card__bar--compacted': bar.turn.afterCompaction }"
              :style="{ height: `${bar.height}%` }"
              :aria-label="describeTurn(bar.turn, bar.index)"
              @mouseenter="hoveredTurn = bar.index"
            />
          </div>
        </template>

        <dl
          v-if="context.lastCall || turnsLeft !== null || cost"
          class="context-card__rows"
        >
          <template v-if="context.lastCall">
            <dt>Read from cache</dt>
            <dd>{{ context.lastCall.cacheRead.toLocaleString() }}</dd>
            <dt>New input</dt>
            <dd>{{ (context.lastCall.input + context.lastCall.cacheWrite).toLocaleString() }}</dd>
            <dt>Output</dt>
            <dd>
              {{ context.lastCall.output.toLocaleString() }}
              <span v-if="context.lastCall.reasoning">· {{ context.lastCall.reasoning.toLocaleString() }} reasoning</span>
            </dd>
          </template>
          <template v-if="turnsLeft !== null">
            <dt>Before it compacts</dt>
            <dd v-if="turnsLeft === 0">
              Probably next turn
            </dd>
            <dd v-else>
              {{ turnsLeft > 25 ? "25+ turns" : `About ${turnsLeft} ${turnsLeft === 1 ? "turn" : "turns"}` }}
              <span>at this pace</span>
            </dd>
          </template>
          <template v-if="cost">
            <dt>Spent this session</dt>
            <dd>{{ cost }}</dd>
          </template>
        </dl>

        <section
          v-if="limits.length"
          class="context-card__limits"
          aria-label="Usage limits"
          data-testid="context-limits"
        >
          <div
            v-for="limit in limits"
            :key="limit.key"
            class="context-card__limit"
            :data-tone="limit.tone"
            data-testid="context-limit"
          >
            <p class="context-card__label context-card__limit-label">
              <span>{{ limit.label }}</span>
              <span class="context-card__limit-value">{{ limit.value }}</span>
            </p>
            <div
              class="context-card__meter context-card__limit-meter"
              aria-hidden="true"
            >
              <div
                class="context-card__meter-fill context-card__limit-fill"
                :style="{ width: `${limit.fill}%` }"
              />
            </div>
          </div>
        </section>

        <p
          v-if="error"
          class="context-card__error"
          data-testid="context-error"
        >
          {{ error }}
        </p>

        <div class="context-card__foot">
          <p class="context-card__why">
            {{ compactHint }}
          </p>
          <button
            type="button"
            class="context-card__compact"
            :class="{ 'context-card__compact--primary': tone !== 'ok' }"
            data-testid="context-compact"
            :disabled="!canCompact"
            :title="compactTitle"
            @click="compact"
          >
            {{ compacting ? "Compacting…" : "Compact now" }}
          </button>
        </div>
      </div>
    </PopoverContent>
  </Popover>
</template>

<style scoped>
.context-ring {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 5px;
  height: 28px;
  min-width: 28px;
  padding: 0;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  font-size: 11.5px;
  font-weight: 500;
  font-variant-numeric: tabular-nums;
  cursor: pointer;
  transition: background var(--transition), color var(--transition);
}

.context-ring:hover,
.context-ring[data-state="open"] {
  background: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

.context-ring--with-percent {
  padding: 0 7px 0 6px;
}

.context-ring__svg {
  width: 16px;
  height: 16px;
  flex: none;
  transform: rotate(-90deg);
  fill: none;
  stroke-width: 2;
}

.context-ring__track {
  stroke: color-mix(in srgb, var(--text) 14%, transparent);
}

.context-ring__fill {
  stroke: color-mix(in srgb, var(--text) 62%, transparent);
  stroke-linecap: round;
  transition: stroke-dasharray 300ms ease-out, stroke 300ms;
}

.context-ring[data-tone="warn"] .context-ring__fill,
.context-ring[data-tone="warn"] .context-ring__percent {
  stroke: var(--idle);
  color: var(--idle);
}

.context-ring[data-tone="danger"] .context-ring__fill,
.context-ring[data-tone="danger"] .context-ring__percent {
  stroke: var(--error);
  color: var(--error);
}

.context-ring--compacting .context-ring__svg {
  animation: context-ring-spin 1.2s linear infinite;
}

.context-card {
  --tone: var(--running);
  font-size: 12.5px;
  color: var(--text);
}

.context-card[data-tone="warn"] {
  --tone: var(--idle);
}

.context-card[data-tone="danger"] {
  --tone: var(--error);
}

.context-card__head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 12px;
}

.context-card__eyebrow {
  margin: 0;
  font-size: 11px;
  color: var(--muted);
}

.context-card__big {
  margin: 0;
  font-size: 26px;
  font-weight: 600;
  line-height: 1.1;
  letter-spacing: -0.02em;
  font-variant-numeric: tabular-nums;
}

.context-card__big small {
  margin-left: 2px;
  font-size: 13px;
  font-weight: 500;
  letter-spacing: 0;
  color: var(--muted);
}

.context-card__state {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-size: 11px;
  font-weight: 500;
  color: var(--muted);
}

.context-card__dot {
  width: 6px;
  height: 6px;
  border-radius: 999px;
  background: var(--tone);
}

.context-card__spin {
  animation: context-ring-spin 1s linear infinite;
}

.context-card__sub {
  margin: 3px 0 0;
  color: var(--muted);
  font-variant-numeric: tabular-nums;
}

.context-card__meter {
  position: relative;
  height: 6px;
  margin: 12px 0 4px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--text) 9%, transparent);
}

.context-card__meter-fill {
  position: absolute;
  inset: 0 auto 0 0;
  border-radius: 999px;
  background: var(--accent);
  transition: width 300ms ease-out;
}

.context-card[data-tone="warn"] .context-card__meter-fill,
.context-card[data-tone="danger"] .context-card__meter-fill {
  background: var(--tone);
}

.context-card__meter-tick {
  position: absolute;
  top: -3px;
  bottom: -3px;
  width: 2px;
  margin-left: -1px;
  border-radius: 2px;
  background: var(--muted);
  opacity: 0.7;
}

.context-card__legend,
.context-card__label {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  font-size: 11px;
  color: var(--muted);
  font-variant-numeric: tabular-nums;
}

.context-card__label {
  margin: 14px 0 6px;
}

.context-card__limits {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-top: 14px;
  padding-top: 12px;
  border-top: 1px solid var(--border);
}

.context-card__label.context-card__limit-label {
  margin: 0;
}

.context-card__limit-value {
  color: var(--text);
}

.context-card__meter.context-card__limit-meter {
  margin: 5px 0 0;
  height: 4px;
}

.context-card__limit[data-tone="ok"] .context-card__limit-fill {
  background: var(--accent);
}

.context-card__limit[data-tone="warn"] .context-card__limit-fill {
  background: var(--status-waiting);
}

.context-card__limit[data-tone="danger"] .context-card__limit-fill {
  background: var(--error);
}

.context-card__chart {
  position: relative;
  display: flex;
  align-items: flex-end;
  gap: 2px;
  height: 54px;
  margin-top: 10px;
}

.context-card__chart-limit {
  position: absolute;
  left: 0;
  right: 0;
  border-top: 1px dashed color-mix(in srgb, var(--text) 25%, transparent);
  pointer-events: none;
}

.context-card__bar {
  position: relative;
  flex: 1;
  min-height: 2px;
  max-width: 18px;
  border-radius: 2px 2px 0 0;
  background: color-mix(in srgb, var(--accent) 55%, transparent);
}

.context-card__bar:hover {
  background: var(--text);
}

.context-card__bar--last {
  background: var(--accent);
}

.context-card[data-tone="warn"] .context-card__bar--last,
.context-card[data-tone="danger"] .context-card__bar--last {
  background: var(--tone);
}

/* A dot above the first bar after a compaction. */
.context-card__bar--compacted::after {
  content: "";
  position: absolute;
  top: -7px;
  left: 50%;
  width: 4px;
  height: 4px;
  margin-left: -2px;
  border-radius: 999px;
  background: var(--muted);
}

.context-card__rows {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 6px 14px;
  margin: 14px 0 0;
}

.context-card__rows dt {
  color: var(--muted);
}

.context-card__rows dd {
  margin: 0;
  text-align: right;
  font-variant-numeric: tabular-nums;
}

.context-card__rows dd span {
  color: var(--muted);
}

.context-card__error {
  margin: 12px 0 0;
  color: var(--error);
  font-size: 12px;
}

.context-card__foot {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 14px;
  padding-top: 12px;
  border-top: 1px solid var(--border);
}

.context-card__why {
  flex: 1;
  margin: 0;
  font-size: 11.5px;
  line-height: 1.4;
  color: var(--muted);
}

.context-card__compact {
  padding: 6px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
  font: inherit;
  font-size: 12px;
  white-space: nowrap;
  cursor: pointer;
  transition: background var(--transition);
}

.context-card__compact:hover:not(:disabled) {
  background: color-mix(in srgb, var(--text) 10%, transparent);
}

.context-card__compact--primary {
  border-color: transparent;
  background: var(--accent);
  color: var(--primary-foreground);
}

.context-card__compact--primary:hover:not(:disabled) {
  background: color-mix(in srgb, var(--accent) 88%, black);
}

.context-card__compact:disabled {
  opacity: 0.5;
  cursor: default;
}

@keyframes context-ring-spin {
  to { transform: rotate(270deg); }
}

@media (prefers-reduced-motion: reduce) {
  .context-ring--compacting .context-ring__svg,
  .context-card__spin {
    animation: none;
  }
}
</style>

<style>
/* In the composer's toolbar the ring takes the free space, so it sits by Send rather than Send on its own. */
.composer-frame__toolbar > .context-ring {
  margin-left: auto;
  margin-right: 2px;
}

.composer-frame__toolbar > .context-ring ~ .composer-frame__send {
  margin-left: 0;
}

/* The popover is teleported out of this component, so its box is styled without scoping. */
.context-popover {
  width: 340px;
  max-width: calc(100vw - 16px);
  padding: 14px 14px 12px;
  border-radius: var(--radius-panel);
  background: var(--card-bg);
  color: var(--text);
}
</style>
