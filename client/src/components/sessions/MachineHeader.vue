<script setup lang="ts">
import { computed } from "vue";
import { ChevronDown } from "lucide-vue-next";

/**
 * A machine's heading in the sessions list. Machines get coral, a colour of their own, so where a session runs
 * never competes with its status colours.
 */
const props = withDefaults(defineProps<{
  name: string;
  /** The machine the app is working in. */
  live?: boolean;
  /** Can't be reached right now; its rows are the last ones it returned. */
  unreachable?: boolean;
  /** Short note on the right: "4s ago", "unreachable". */
  note?: string | null;
  /** Collapsible groups show a chevron and report their state. */
  expanded?: boolean | null;
  count?: number | null;
}>(), { live: false, unreachable: false, note: null, expanded: null, count: null });

const emit = defineEmits<{ toggle: [] }>();

const tag = computed(() => (props.expanded === null || props.expanded === undefined ? "div" : "button"));
</script>

<template>
  <component
    :is="tag"
    :type="tag === 'button' ? 'button' : undefined"
    class="machine-header"
    :class="{
      'machine-header--button': tag === 'button',
      'machine-header--collapsed': expanded === false,
    }"
    :aria-expanded="tag === 'button' ? expanded : undefined"
    data-testid="machine-header"
    :data-machine-live="live ? 'true' : undefined"
    @click="tag === 'button' && emit('toggle')"
  >
    <ChevronDown
      v-if="tag === 'button'"
      class="machine-header__chevron"
      aria-hidden="true"
    />
    <span
      class="machine-header__dot"
      :class="{ 'machine-header__dot--down': unreachable }"
      aria-hidden="true"
    />
    <span class="machine-header__name">{{ name }}</span>
    <span
      v-if="live"
      class="machine-header__live"
      title="The machine you're working in"
    >Live</span>
    <span
      v-if="note"
      class="machine-header__note"
      :class="{ 'machine-header__note--down': unreachable }"
    >{{ note }}</span>
    <span
      v-else-if="count !== null && count !== undefined"
      class="machine-header__note"
    >{{ count }}</span>
  </component>
</template>

<style scoped>
.machine-header {
  width: 100%;
  min-height: 28px;
  display: flex;
  align-items: center;
  gap: 7px;
  margin-top: 6px;
  padding: 0 10px 0 8px;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--text);
  text-align: left;
  font: inherit;
}

/* The live machine has no chevron; indent it so every machine's dot sits on one line. */
.machine-header:not(.machine-header--button) {
  padding-left: 23px;
}

.machine-header--button {
  cursor: pointer;
  padding-left: 4px;
  transition: background var(--transition);
}

.machine-header--button:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.machine-header--button:hover .machine-header__name {
  color: var(--coral);
}

.machine-header--button:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.machine-header__chevron {
  width: 12px;
  height: 12px;
  flex-shrink: 0;
  color: color-mix(in srgb, var(--muted) 70%, transparent);
  transition: transform var(--transition);
}

.machine-header--collapsed .machine-header__chevron {
  transform: rotate(-90deg);
}

.machine-header__dot {
  width: 7px;
  height: 7px;
  flex-shrink: 0;
  border-radius: 50%;
  background: var(--running);
}

.machine-header__dot--down {
  background: var(--error);
}

.machine-header__name {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: var(--font-mono, ui-monospace, monospace);
  font-size: 11.5px;
  font-weight: 600;
  letter-spacing: 0.02em;
  transition: color var(--transition);
}

.machine-header__live {
  flex-shrink: 0;
  padding: 1px 5px;
  border-radius: 4px;
  background: color-mix(in srgb, var(--coral) 14%, transparent);
  color: var(--coral);
  font-family: var(--font-mono, ui-monospace, monospace);
  font-size: 9.5px;
  font-weight: 700;
  letter-spacing: 0.09em;
  text-transform: uppercase;
}

.machine-header__note {
  margin-left: auto;
  flex-shrink: 0;
  font-size: 11px;
  color: color-mix(in srgb, var(--muted) 80%, transparent);
  font-variant-numeric: tabular-nums;
}

.machine-header__note--down {
  color: var(--error);
}
</style>
