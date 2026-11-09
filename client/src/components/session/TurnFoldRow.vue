<script setup lang="ts">
import { computed } from "vue";
import { Check, ChevronRight } from "lucide-vue-next";
import { formatCallDuration } from "@/lib/tool-row";
import { summaryParts, type FoldedTurn } from "@/lib/turn-fold";

/**
 * A finished turn as one line: "Worked for 34s · read 4 files · edited 2 · ran 2 commands" and a ✓. It opens to the
 * turn's own rows; this is the one tick the rows no longer repeat.
 */
const props = defineProps<{
  turn: FoldedTurn;
  open: boolean;
}>();

defineEmits<{
  toggle: [];
}>();

const worked = computed(() => {
  const took = formatCallDuration(props.turn.summary.durationMs);
  // Whole seconds read better here than tenths: "Worked for 34s".
  return took.startsWith("0.") ? "Worked briefly" : `Worked for ${took}`.replace(/ for $/, "");
});
const details = computed(() => summaryParts(props.turn.summary).join(" · "));
</script>

<template>
  <button
    type="button"
    class="turn-fold"
    :aria-expanded="open"
    data-testid="turn-fold"
    @click="$emit('toggle')"
  >
    <ChevronRight
      class="turn-fold__chevron"
      aria-hidden="true"
    />
    <span class="turn-fold__worked">{{ worked }}</span>
    <span
      v-if="details"
      class="turn-fold__details"
    >· {{ details }}</span>
    <Check
      class="turn-fold__done"
      aria-label="Done"
    />
  </button>
</template>

<style scoped>
.turn-fold {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  min-height: 30px;
  margin: 2px 0;
  padding: 0 8px;
  border: 0;
  border-radius: calc(var(--radius-btn) - 2px);
  background: transparent;
  color: var(--muted);
  font: inherit;
  font-size: 13px;
  text-align: left;
  cursor: pointer;
  transition: background var(--transition), color var(--transition);
}

.turn-fold:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.turn-fold:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.turn-fold__chevron {
  width: 12px;
  height: 12px;
  flex-shrink: 0;
  transition: transform var(--transition);
}

.turn-fold[aria-expanded="true"] .turn-fold__chevron {
  transform: rotate(90deg);
}

.turn-fold__worked {
  flex-shrink: 0;
  font-weight: 500;
  color: var(--text);
}

.turn-fold__details {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.turn-fold__done {
  width: 13px;
  height: 13px;
  margin-left: auto;
  flex-shrink: 0;
  color: var(--running);
}
</style>
