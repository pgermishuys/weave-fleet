<script setup lang="ts">
import { computed } from "vue";
import { LoaderCircle } from "lucide-vue-next";
import { describeVersionLine, effectiveRule, isUpdateRule, UPDATE_RULES, type FleetUpdateRow, type Rollout, type UpdateRule } from "@/lib/fleet-updates";

/**
 * The line along the bottom of a machine card in Settings → Updates & about › Machines (with Updates in Machines on): the version it
 * runs, where it is with updating, and the rule it follows.
 */
const props = defineProps<{
  row: FleetUpdateRow;
  rollout: Rollout | null;
  /** The rule chosen for every machine; null until the user chooses one. */
  chosenRule: UpdateRule | null;
}>();
const emit = defineEmits<{ override: [value: UpdateRule | null] }>();

const line = computed(() => describeVersionLine(props.row, props.rollout));

const toneClass: Record<(typeof line.value)["tone"], string> = {
  ok: "text-[var(--running)]",
  warn: "text-[var(--idle)]",
  info: "text-[var(--complete)]",
  bad: "text-[var(--error)]",
  muted: "text-muted",
};

/** What "Default" means on this card: the rule chosen for all machines, or the one that suits its kind. */
const defaultLabel = computed(() => {
  const rule = effectiveRule(null, props.chosenRule, props.row.kind === "node" ? "node" : "fleet");
  return UPDATE_RULES.find((candidate) => candidate.value === rule)?.short ?? rule;
});

const spinning = computed(() => ["downloading", "checking", "waiting", "restarting"].includes(props.row.state.kind));

function onChange(event: Event): void {
  const value = (event.target as HTMLSelectElement).value;
  emit("override", isUpdateRule(value) ? value : null);
}
</script>

<template>
  <div
    class="machine-card__version"
    data-testid="machine-version"
    :data-state="row.state.kind"
  >
    <span data-testid="machine-version-text">{{ line.version }}<template v-if="line.target"> → {{ line.target }}</template></span>
    <span aria-hidden="true">·</span>
    <span
      :class="toneClass[line.tone]"
      data-testid="machine-version-state"
    >
      <LoaderCircle
        v-if="spinning"
        :size="11"
        class="mr-1 inline animate-spin"
        aria-hidden="true"
      />{{ line.text }}
    </span>
    <label
      v-if="line.ruleMenu && row.rule"
      class="ml-auto inline-flex min-w-0 max-w-full items-center gap-1.5"
    >
      Updates:
      <select
        :value="row.rule.overridden ?? 'default'"
        class="min-w-0 rounded-btn border border-border bg-card-bg px-2 py-1 text-xs text-text"
        :aria-label="`When ${row.name} updates`"
        data-testid="update-rule-select"
        @change="onChange"
      >
        <option value="default">
          Default ({{ defaultLabel }})
        </option>
        <option
          v-for="rule in UPDATE_RULES"
          :key="rule.value"
          :value="rule.value"
          :title="rule.detail"
        >
          {{ rule.short }}
        </option>
      </select>
    </label>
  </div>
</template>

<style scoped>
.machine-card__version {
  display: flex;
  flex: 1 1 100%;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 8px;
  margin-top: 6px;
  font-family: var(--font-mono);
  font-size: 11.5px;
  color: var(--muted);
}
</style>
