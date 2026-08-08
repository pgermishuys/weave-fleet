<script setup lang="ts">
import { Plus, Loader2 } from "lucide-vue-next";
import { useAutomations } from "@/composables/use-automations";

interface Props {
  modelValue: string | null;
}

interface Emits {
  "update:modelValue": [value: string];
  "create": [];
}

defineProps<Props>();
const emit = defineEmits<Emits>();

const { automations, isLoading } = useAutomations();

function selectAutomation(id: string): void {
  emit("update:modelValue", id);
}

function handleCreate(): void {
  emit("create");
}
</script>

<template>
  <section
    class="automations-nav-panel"
    aria-label="Automations navigation"
  >
    <div class="panel-header-row">
      <p class="panel-header">
        Automations
      </p>
    </div>

    <div class="automations-nav-actions">
      <button
        type="button"
        class="new-automation-btn"
        @click="handleCreate"
      >
        <Plus :size="16" aria-hidden="true" />
        <span>New Automation</span>
      </button>
    </div>

    <nav
      v-if="!isLoading && automations.length > 0"
      class="automations-nav"
      aria-label="Automations list"
    >
      <button
        v-for="automation in automations"
        :key="automation.id"
        type="button"
        class="automations-nav__item"
        :class="{ 'automations-nav__item--active': modelValue === automation.id }"
        :aria-current="modelValue === automation.id ? 'page' : undefined"
        @click="selectAutomation(automation.id)"
      >
        <span
          class="status-dot"
          :class="automation.isEnabled ? 'status-dot--enabled' : 'status-dot--disabled'"
          :aria-label="automation.isEnabled ? 'Enabled' : 'Disabled'"
        />
        <span class="automation-name">{{ automation.name }}</span>
      </button>
    </nav>

    <div
      v-else-if="isLoading"
      class="automations-nav-empty"
    >
      <Loader2
        :size="20"
        class="spinner"
        aria-hidden="true"
      />
      <span class="sr-only">Loading automations...</span>
    </div>

    <div
      v-else
      class="automations-nav-empty"
    >
      <p class="empty-message">
        No automations yet
      </p>
    </div>
  </section>
</template>

<style scoped>
.automations-nav-panel {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  background: var(--panel-bg);
}

.panel-header-row {
  padding-top: 4px;
}

.panel-header {
  margin: 0;
  padding: 14px 16px 10px;
  font-size: 10px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--muted);
}

.automations-nav-actions {
  padding: 0 12px 12px;
}

.new-automation-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  width: 100%;
  min-height: 36px;
  padding: 0 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--bg);
  color: var(--text);
  font-size: 12px;
  font-weight: 500;
  transition: background-color var(--transition), border-color var(--transition);
}

.new-automation-btn:hover {
  border-color: var(--accent);
  background: color-mix(in srgb, var(--bg) 92%, var(--accent) 8%);
}

.new-automation-btn:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.automations-nav {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 4px;
  padding: 0 12px 12px;
  overflow-y: auto;
}

.automations-nav__item {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  min-height: 40px;
  padding: 0 12px;
  border: 1px solid transparent;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  font-size: 12px;
  font-weight: 500;
  text-align: left;
  transition: background-color var(--transition), border-color var(--transition), color var(--transition);
}

.automations-nav__item:hover {
  background: var(--bg);
  color: var(--text);
}

.automations-nav__item:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.automations-nav__item--active {
  border-color: color-mix(in srgb, var(--accent) 40%, transparent);
  background: color-mix(in srgb, var(--panel-bg) 88%, var(--accent) 12%);
  color: var(--text);
}

.status-dot {
  flex-shrink: 0;
  width: 8px;
  height: 8px;
  border-radius: 50%;
}

.status-dot--enabled {
  background: var(--color-success);
}

.status-dot--disabled {
  background: var(--color-text-tertiary);
}

.automation-name {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.automations-nav-empty {
  display: flex;
  flex: 1;
  align-items: center;
  justify-content: center;
  padding: 24px 12px;
}

.spinner {
  animation: spin 1s linear infinite;
  color: var(--muted);
}

@keyframes spin {
  from {
    transform: rotate(0deg);
  }
  to {
    transform: rotate(360deg);
  }
}

.empty-message {
  margin: 0;
  font-size: 12px;
  color: var(--muted);
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border-width: 0;
}
</style>
