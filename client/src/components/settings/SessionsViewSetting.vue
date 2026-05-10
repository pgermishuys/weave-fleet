<script setup lang="ts">
import { useSessionsViewMode, type SessionsViewMode } from "@/composables/use-sessions-view-mode";

const { viewMode, setViewMode } = useSessionsViewMode();

interface ModeOption {
  value: SessionsViewMode;
  label: string;
  description: string;
}

const options: readonly ModeOption[] = [
  {
    value: "v1",
    label: "Workspaces (V1)",
    description: "Sessions grouped by workspace directory — classic fleet view.",
  },
  {
    value: "v2",
    label: "Projects (V2)",
    description: "Sessions organized into user-defined projects.",
  },
  {
    value: "both",
    label: "Both",
    description: "Show both Workspaces and Projects rail icons simultaneously.",
  },
];
</script>

<template>
  <section class="rounded-card border border-border bg-card-bg p-6 shadow-sm">
    <div class="flex flex-col gap-1">
      <h2 class="text-lg font-semibold text-text">
        Sessions View
      </h2>
      <p class="text-sm text-muted">
        Choose how sessions are organized in the sidebar and dashboard.
      </p>
    </div>

    <div class="mt-5 flex flex-col gap-2">
      <label
        v-for="option in options"
        :key="option.value"
        class="mode-option"
        :class="{ 'mode-option--active': viewMode === option.value }"
      >
        <input
          type="radio"
          name="sessions-view-mode"
          :value="option.value"
          :checked="viewMode === option.value"
          class="sr-only"
          @change="setViewMode(option.value)"
        >
        <div class="mode-option-text">
          <span class="mode-option-label">{{ option.label }}</span>
          <span class="mode-option-desc">{{ option.description }}</span>
        </div>
        <div
          class="mode-option-indicator"
          aria-hidden="true"
        />
      </label>
    </div>
  </section>
</template>

<style scoped>
.mode-option {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px 14px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  cursor: pointer;
  transition: border-color 0.15s ease, background-color 0.15s ease;
}

.mode-option:hover {
  border-color: color-mix(in srgb, var(--accent) 50%, transparent);
}

.mode-option--active {
  border-color: var(--accent);
  background: color-mix(in srgb, var(--accent) 8%, transparent);
}

.mode-option-text {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.mode-option-label {
  font-size: 13px;
  font-weight: 500;
  color: var(--text);
}

.mode-option-desc {
  font-size: 11px;
  color: var(--muted);
  line-height: 1.4;
}

.mode-option-indicator {
  width: 14px;
  height: 14px;
  border-radius: 50%;
  border: 2px solid var(--border);
  flex-shrink: 0;
  transition: border-color 0.15s ease, background-color 0.15s ease;
}

.mode-option--active .mode-option-indicator {
  border-color: var(--accent);
  background: var(--accent);
  box-shadow: inset 0 0 0 3px var(--card-bg, var(--panel-bg));
}
</style>
