<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { Check, ChevronLeft, ChevronRight, LoaderCircle, X } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import type { FoldedStep } from "@/lib/phone/fold-steps";

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
    @close="emit('close')"
  >
    <template v-if="!picked">
      <h2 class="ss__title">
        Steps
      </h2>
      <ul class="ss__list">
        <li
          v-for="step in steps"
          :key="step.id"
        >
          <button
            type="button"
            class="ss__row"
            data-testid="phone-step"
            @click="picked = step"
          >
            <LoaderCircle
              v-if="step.status === 'running' || step.status === 'pending'"
              class="animate-spin text-muted"
              :size="14"
              aria-hidden="true"
            />
            <X
              v-else-if="step.status === 'error'"
              class="text-error"
              :size="14"
              aria-hidden="true"
            />
            <Check
              v-else
              class="text-running"
              :size="14"
              aria-hidden="true"
            />
            <span class="ss__tool">{{ step.tool }}</span>
            <span class="ss__label">{{ step.label }}</span>
            <ChevronRight
              :size="14"
              class="text-muted"
              aria-hidden="true"
            />
          </button>
        </li>
      </ul>
    </template>
    <template v-else>
      <button
        type="button"
        class="ss__back"
        @click="picked = null"
      >
        <ChevronLeft
          :size="16"
          aria-hidden="true"
        /> Steps
      </button>
      <h2 class="ss__title">
        <span class="ss__tool">{{ picked.tool }}</span> {{ picked.label }}
      </h2>
      <pre
        class="ss__pre"
        :class="{ 'ss__pre--diff': detail.kind === 'diff' }"
        data-testid="phone-step-detail"
      ><template v-if="detail.kind === 'diff'"><span
        v-for="(line, index) in detail.text.split('\n')"
        :key="index"
        :class="line.startsWith('+') ? 'ss__add' : 'ss__del'"
      >{{ line }}
</span></template><template v-else>{{ detail.text }}</template></pre>
    </template>
  </BottomSheet>
</template>

<style scoped>
.ss__title {
  margin-bottom: 8px;
  font-size: 15px;
  font-weight: 600;
  overflow-wrap: anywhere;
}

.ss__list {
  display: grid;
}

.ss__row {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  min-height: 44px;
  padding: 6px 2px;
  border: 0;
  border-bottom: 1px solid var(--border);
  background: transparent;
  color: var(--text);
  font: inherit;
  font-size: 13px;
  text-align: left;
  cursor: pointer;
}

.ss__tool {
  font-family: var(--font-mono-stack);
  font-size: 11px;
  color: var(--muted);
}

.ss__label {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.ss__back {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  min-height: 36px;
  margin-bottom: 4px;
  border: 0;
  background: transparent;
  color: var(--accent);
  font: inherit;
  font-size: 13px;
  cursor: pointer;
}

.ss__pre {
  overflow-x: auto;
  margin: 0;
  padding: 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--main-bg);
  font-family: var(--font-mono-stack);
  font-size: 12px;
  line-height: 1.5;
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
