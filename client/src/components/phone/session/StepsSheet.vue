<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { ArrowLeft, ChevronLeft, LoaderCircle } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import type { FoldedStep } from "@/lib/phone/fold-steps";
import { phoneLook } from "@/composables/phone/use-phone-env";

/** The steps of a folded row, then one step's diff or output. Read-only. */
const props = defineProps<{ open: boolean; steps: readonly FoldedStep[] }>();
const emit = defineEmits<{ (event: "close"): void }>();

const picked = shallowRef<FoldedStep | null>(null);
watch(() => props.open, (open) => {
  if (!open) picked.value = null;
});

function asRecord(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
}

const VERBS: Record<FoldedStep["category"], string> = { read: "Read", edit: "Edited", run: "Ran", search: "Searched", other: "Used" };
/** "Read", "Ran · running", "Edited · failed": the line over each step. */
function stepLine(step: FoldedStep): string {
  const verb = step.category === "other" ? `Used ${step.tool}` : VERBS[step.category];
  if (step.status === "running" || step.status === "pending") return `${verb} · running`;
  if (step.status === "error") return `${verb} · failed`;
  return verb;
}

/** An edit as -/+ lines; anything else as its output. */
const detail = computed(() => {
  const step = picked.value;
  if (!step) return { kind: "none" as const, text: "" };
  const state = asRecord(step.part.state);
  const input = asRecord(state?.input);
  if (step.category === "edit" && typeof input?.oldString === "string" && typeof input?.newString === "string") {
    const lines = [
      ...input.oldString.split("\n").map((line) => `- ${line}`),
      ...input.newString.split("\n").map((line) => `+ ${line}`),
    ];
    return { kind: "diff" as const, text: lines.join("\n") };
  }
  if (step.category === "edit" && typeof input?.content === "string") {
    return { kind: "diff" as const, text: input.content.split("\n").map((line) => `+ ${line}`).join("\n") };
  }
  const output = state?.output ?? state?.error;
  const text = typeof output === "string" ? output : output == null ? "" : JSON.stringify(output, null, 2);
  return { kind: "output" as const, text: text || (step.status === "running" ? "Still running…" : "No output.") };
});
</script>

<template>
  <BottomSheet
    :open="open"
    :label="picked ? picked.label : 'Steps'"
    :detents="['medium', 'large']"
    initial="medium"
    :title="picked ? undefined : 'Steps'"
    @close="emit('close')"
  >
    <template
      v-if="picked"
      #head
    >
      <button
        type="button"
        class="ph-navbtn ph-glass"
        aria-label="Back to the steps"
        @click="picked = null"
      >
        <ArrowLeft
          v-if="phoneLook === 'android'"
          :size="24"
          aria-hidden="true"
        />
        <ChevronLeft
          v-else
          :size="24"
          :stroke-width="2.4"
          aria-hidden="true"
        />
      </button>
      <h2>{{ stepLine(picked) }}</h2>
    </template>

    <div
      v-if="!picked"
      class="ph-group"
    >
      <button
        v-for="step in steps"
        :key="step.id"
        type="button"
        class="ph-row"
        data-testid="phone-step"
        @click="picked = step"
      >
        <span class="ph-row__main">
          <span
            class="ph-row__sub ss__verb"
            :class="{ 'ss__verb--bad': step.status === 'error' }"
          >{{ stepLine(step) }}</span>
          <span class="ph-row__title ss__label">{{ step.label }}</span>
        </span>
        <LoaderCircle
          v-if="step.status === 'running' || step.status === 'pending'"
          class="ph-spinner"
          :size="18"
          aria-hidden="true"
        />
      </button>
    </div>
    <div
      v-else
      class="ph-sheet__pad"
    >
      <p class="ss__title">
        {{ picked.label }}
      </p>
      <pre
        class="ph-code ss__pre"
        data-testid="phone-step-detail"
      ><template v-if="detail.kind === 'diff'"><span
        v-for="(line, index) in detail.text.split('\n')"
        :key="index"
        :class="line.startsWith('+') ? 'ss__add' : 'ss__del'"
      >{{ line }}
</span></template><template v-else>{{ detail.text }}</template></pre>
    </div>
  </BottomSheet>
</template>

<style scoped>
.ss__verb {
  margin: 0 0 2px;
  font-size: var(--ph-t-foot);
}

.ss__verb--bad {
  color: var(--error);
}

.ss__label {
  font-family: var(--ph-mono);
  font-size: 0.85rem;
}

.ss__title {
  margin: 0 0 10px;
  font-family: var(--ph-mono);
  font-size: 0.85rem;
  overflow-wrap: anywhere;
}

.ss__pre {
  overflow-x: auto;
  font-size: 0.75rem;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.ss__add {
  color: var(--diff-add);
}

.ss__del {
  color: var(--diff-del);
}
</style>
