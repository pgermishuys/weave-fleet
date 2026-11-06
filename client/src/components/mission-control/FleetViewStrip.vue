<script setup lang="ts">
import { useFleetMenu } from "@/composables/use-fleet-menu";

// The fleet-menu on a phone, where the context panel is a drawer: Needs you and the machines as chips.
const menu = useFleetMenu();
</script>

<template>
  <nav
    class="fleet-strip"
    aria-label="Fleet views"
  >
    <button
      v-for="item in [menu.attention.value, ...menu.machineItems.value]"
      :key="item.id"
      type="button"
      class="fleet-strip__chip"
      :class="{ 'fleet-strip__chip--active': menu.isActive(item) }"
      :aria-current="menu.isActive(item) ? 'page' : undefined"
      @click="menu.select(item)"
    >
      {{ item.label }}
      <span class="fleet-strip__count">{{ item.count }}</span>
    </button>
  </nav>
</template>

<style scoped>
.fleet-strip {
  display: flex;
  gap: 6px;
  overflow-x: auto;
  padding-bottom: 2px;
}

.fleet-strip__chip {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  gap: 6px;
  height: 32px;
  padding: 0 12px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--card-bg);
  color: var(--muted);
  font-size: 12.5px;
  font-weight: 500;
}

.fleet-strip__chip--active {
  border-color: color-mix(in srgb, var(--accent) 40%, transparent);
  color: var(--text);
}

.fleet-strip__count {
  font-variant-numeric: tabular-nums;
}
</style>
