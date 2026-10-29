<script setup lang="ts">
import { computed } from "vue";
import { RefreshCw } from "lucide-vue-next";
import {
  describeReleaseLine,
  describeRolloutNarrative,
  UPDATE_RULES,
  isUpdateRule,
  type FleetUpdateState,
  type Rollout,
  type UpdateRule,
} from "@/lib/fleet-updates";

/**
 * The release line at the top of Settings → Updates & about › Machines (with Updates in Machines on): what is out, how many machines have
 * it, the rule every machine follows unless it has its own, Check now and Update machines now; or, while a rollout
 * runs, where it is and how to stop it.
 */
const props = defineProps<{
  summary: { latest: string | null; waiting: number; downloaded: number };
  checkedAt: string | null;
  rollout: Rollout | null;
  rolloutRunning: boolean;
  isChecking: boolean;
  readyMachines: number;
  /** Keys of the machines that can't be reached now. */
  unreachable: ReadonlySet<string>;
  chosenRule: UpdateRule | null;
  outcome: { text: string; tone: FleetUpdateState["tone"] } | null;
  actionError: string | null;
}>();
const emit = defineEmits<{
  check: [];
  update: [];
  stop: [];
  choose: [rule: UpdateRule | null];
  whatsNew: [version: string];
}>();

const buttonSecondaryClass = "inline-flex items-center justify-center gap-2 rounded-btn border border-border bg-card-bg px-3 py-1.5 text-sm font-medium text-text transition-colors hover:border-accent/50 disabled:cursor-not-allowed disabled:opacity-60";

const release = computed(() => describeReleaseLine(props.summary, props.checkedAt));
const narrative = computed(() => (props.rolloutRunning && props.rollout ? describeRolloutNarrative(props.rollout, props.unreachable) : null));

/** "" is no chosen rule: a node updates when nothing is working on it and a full Fleet asks first. */
const RULE_OPTIONS: { value: string; label: string; detail: string }[] = [
  { value: "", label: "Default: nodes when idle, Fleets ask", detail: "Until you choose, a node updates when nothing is working on it and a full Fleet asks first." },
  { value: "ask", label: "Ask me first, every machine", detail: UPDATE_RULES[0]!.detail },
  { value: "idle", label: "When nothing is working, every machine", detail: UPDATE_RULES[1]!.detail },
  { value: "overnight", label: "Only overnight, 02:00–05:00", detail: UPDATE_RULES[2]!.detail },
];
const ruleDetail = computed(() => RULE_OPTIONS.find((option) => option.value === (props.chosenRule ?? ""))?.detail ?? "");

function onChoose(event: Event): void {
  const value = (event.target as HTMLSelectElement).value;
  emit("choose", isUpdateRule(value) ? value : null);
}

const outcomeClass: Record<FleetUpdateState["tone"], string> = {
  ok: "border-[var(--running)]/40 text-[var(--running)]",
  warn: "border-[var(--idle)]/40 text-[var(--idle)]",
  info: "border-[var(--complete)]/40 text-[var(--complete)]",
  bad: "border-[var(--error)]/40 text-[var(--error)]",
  muted: "border-border text-muted",
};
</script>

<template>
  <div
    class="mt-5"
    data-testid="machines-updates"
  >
    <div
      class="flex flex-wrap items-center gap-x-4 gap-y-2 rounded-card border border-border bg-main-bg px-4 py-3"
      :data-kind="narrative ? 'rolling' : release.kind"
      data-testid="machines-updates-strip"
    >
      <template v-if="narrative">
        <p class="min-w-0 flex-1 basis-64 text-sm text-text">
          <b class="font-semibold">{{ narrative.title }}</b>
          {{ " " }}<span class="text-muted">{{ narrative.text }}</span>
        </p>
        <button
          type="button"
          :class="buttonSecondaryClass"
          data-testid="updates-stop-rollout"
          @click="emit('stop')"
        >
          {{ narrative.active ? `Stop after ${narrative.active}` : "Stop after this one" }}
        </button>
      </template>

      <template v-else>
        <p
          class="min-w-0 flex-1 basis-64 text-sm"
          :class="release.kind === 'available' ? 'text-text' : 'text-muted'"
          data-testid="updates-headline"
        >
          <b
            v-if="release.lead"
            class="font-semibold"
          >{{ release.lead }}</b>
          {{ " " }}<span :class="release.kind === 'available' ? 'text-muted' : ''">{{ release.text }}</span>
          {{ " " }}<button
            v-if="release.kind === 'available' && summary.latest"
            type="button"
            class="text-muted underline underline-offset-2 transition-colors hover:text-text"
            data-testid="updates-whats-new"
            @click="emit('whatsNew', summary.latest)"
          >
            What's new
          </button>
        </p>
        <label
          v-if="release.kind !== 'none'"
          class="inline-flex items-center gap-1.5 text-xs text-muted"
        >
          Machines update:
          <select
            :value="chosenRule ?? ''"
            class="rounded-btn border border-border bg-card-bg px-2 py-1 text-xs text-text"
            aria-label="When machines update"
            data-testid="updates-default-rule"
            @change="onChoose"
          >
            <option
              v-for="option in RULE_OPTIONS"
              :key="option.value"
              :value="option.value"
              :title="option.detail"
            >
              {{ option.label }}
            </option>
          </select>
        </label>
        <div class="ml-auto flex shrink-0 flex-wrap items-center gap-2">
          <button
            type="button"
            :disabled="isChecking"
            :class="buttonSecondaryClass"
            data-testid="updates-check-now"
            @click="emit('check')"
          >
            <RefreshCw
              :size="14"
              :class="{ 'animate-spin': isChecking }"
              aria-hidden="true"
            />
            Check now
          </button>
          <button
            v-if="release.kind === 'available' && readyMachines"
            type="button"
            class="inline-flex items-center justify-center gap-2 rounded-btn bg-accent px-3 py-1.5 text-sm font-medium text-white transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-50"
            data-testid="updates-update-machines"
            @click="emit('update')"
          >
            Update machines now ({{ readyMachines }})
          </button>
        </div>
      </template>
    </div>

    <p
      v-if="!narrative && release.kind !== 'none'"
      class="mt-2 text-xs text-muted"
      data-testid="updates-rule-hint"
    >
      {{ ruleDetail }} Update machines now doesn't wait for the rule.
    </p>
    <p
      v-if="outcome && !rolloutRunning"
      class="mt-3 rounded-btn border px-3 py-2 font-mono text-[12px]"
      :class="outcomeClass[outcome.tone]"
      data-testid="updates-outcome"
    >
      {{ outcome.text }}
    </p>
    <p
      v-if="actionError"
      class="mt-3 rounded-btn border border-[var(--error)]/40 px-3 py-2 font-mono text-[12px] text-[var(--error)]"
      data-testid="updates-error"
    >
      {{ actionError }}
    </p>
  </div>
</template>
