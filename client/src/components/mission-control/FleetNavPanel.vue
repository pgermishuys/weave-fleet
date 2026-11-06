<script setup lang="ts">
import { useFleetMenu } from "@/composables/use-fleet-menu";

const menu = useFleetMenu();
</script>

<template>
  <section
    class="fleet-nav"
    aria-label="Fleet navigation"
  >
    <p class="fleet-nav__header">
      Fleet
    </p>

    <nav
      class="fleet-nav__sections"
      aria-label="Fleet views"
    >
      <div
        class="fleet-nav__group"
        role="group"
        aria-label="Attention"
      >
        <p class="fleet-nav__group-label">
          Attention
        </p>
        <button
          type="button"
          class="fleet-nav__item"
          :class="{ 'fleet-nav__item--active': menu.isActive(menu.attention.value) }"
          :aria-current="menu.isActive(menu.attention.value) ? 'page' : undefined"
          data-fleet-view="needs-you"
          @click="menu.select(menu.attention.value)"
        >
          <span>{{ menu.attention.value.label }}</span>
          <span
            class="fleet-nav__count"
            :class="{ 'fleet-nav__count--alert': menu.attention.value.count > 0 }"
          >{{ menu.attention.value.count }}</span>
        </button>
      </div>

      <div
        class="fleet-nav__group"
        role="group"
        aria-label="Machines"
      >
        <p class="fleet-nav__group-label">
          Machines
        </p>
        <button
          v-for="item in menu.machineItems.value"
          :key="item.id"
          type="button"
          class="fleet-nav__item"
          :class="{ 'fleet-nav__item--active': menu.isActive(item) }"
          :aria-current="menu.isActive(item) ? 'page' : undefined"
          :data-fleet-view="`machine:${item.id}`"
          @click="menu.select(item)"
        >
          <span class="fleet-nav__name">{{ item.label }}</span>
          <span
            v-if="item.unreachable"
            class="fleet-nav__note"
          >offline</span>
          <span class="fleet-nav__count">{{ item.count }}</span>
        </button>
      </div>
    </nav>
  </section>
</template>

<style scoped>
.fleet-nav {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  overflow-y: auto;
  padding: 0 12px 12px;
}

.fleet-nav__header {
  margin: 0;
  padding: 18px 4px 10px;
  font-size: 0.71429rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--muted);
}

.fleet-nav__group {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin-bottom: 8px;
}

.fleet-nav__group-label {
  margin: 8px 0 2px;
  padding: 0 12px;
  font-size: 0.71429rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--muted);
  opacity: 0.8;
}

.fleet-nav__item {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  min-height: 36px;
  padding: 0 12px;
  border: 1px solid transparent;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  font-size: 0.85714rem;
  font-weight: 500;
  text-align: left;
  transition: background-color var(--transition), border-color var(--transition), color var(--transition);
}

.fleet-nav__item:hover {
  background: var(--bg);
  color: var(--text);
}

.fleet-nav__item:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.fleet-nav__item--active {
  border-color: color-mix(in srgb, var(--accent) 40%, transparent);
  background: color-mix(in srgb, var(--panel-bg) 88%, var(--accent) 12%);
  color: var(--text);
}

.fleet-nav__name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.fleet-nav__item > span:first-child {
  flex: 1;
}

.fleet-nav__note {
  font-size: 0.71429rem;
  color: var(--muted);
}

.fleet-nav__count {
  font-size: 0.78571rem;
  font-variant-numeric: tabular-nums;
  color: var(--muted);
}

.fleet-nav__count--alert {
  font-weight: 600;
  color: var(--accent);
}
</style>
