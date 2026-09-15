<script setup lang="ts">
import { computed } from "vue";
import { Loader2, Pencil, Plus } from "lucide-vue-next";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import { useAutomations } from "@/composables/use-automations";
import { useAutomationsNav } from "@/composables/use-automations-nav";
import { useRelativeTime } from "@/composables/use-relative-time";
import { autoName, parseSchedule, promptFrom } from "@/lib/automation-schedule";
import { automationRowStatus } from "@/lib/automations";

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
const { viewMode, draft } = useAutomationsNav();
const now = useRelativeTime();

/** The draft's row follows what's typed, like a new session's. */
const draftTitle = computed(() => {
  const text = draft.text;
  const hit = draft.manualWhen ? null : parseSchedule(text);
  return draft.name.trim() || autoName(promptFrom(text, hit)) || "New automation";
});

const rows = computed(() => automations.value.map((automation) => ({
  automation,
  status: automationRowStatus(automation, new Date(now.value)),
})));

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
        data-testid="new-automation"
        @click="handleCreate"
      >
        <Plus
          :size="16"
          aria-hidden="true"
        />
        <span>New automation</span>
      </button>
    </div>

    <nav
      v-if="viewMode === 'create' || (!isLoading && automations.length > 0)"
      class="automations-nav"
      aria-label="Automations list"
    >
      <button
        v-if="viewMode === 'create'"
        type="button"
        class="automation-row automation-row--active"
        aria-current="page"
        data-testid="automation-draft-row"
      >
        <Pencil
          class="automation-row__draft-icon"
          aria-hidden="true"
        />
        <span class="automation-row__title">{{ draftTitle }}</span>
        <span class="automation-row__meta">Draft</span>
      </button>
      <button
        v-for="{ automation, status } in rows"
        :key="automation.id"
        type="button"
        class="automation-row"
        :class="{
          'automation-row--active': modelValue === automation.id && viewMode === 'edit',
          'automation-row--off': !automation.isEnabled,
        }"
        :aria-current="modelValue === automation.id && viewMode === 'edit' ? 'page' : undefined"
        data-testid="automation-row"
        @click="selectAutomation(automation.id)"
      >
        <StatusGlyph
          v-if="status.glyph === 'working'"
          status="active"
          label="Running"
        />
        <StatusGlyph
          v-else-if="status.glyph === 'error'"
          status="error"
          label="Its last run failed"
        />
        <span
          v-else
          class="automation-row__glyph-slot"
          aria-hidden="true"
        />
        <span class="automation-row__title">{{ automation.name }}</span>
        <span
          v-if="status.label"
          class="automation-row__meta"
          :class="`automation-row__meta--${status.tone}`"
          data-testid="automation-row-status"
        >{{ status.label }}</span>
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
  background: transparent;
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
  gap: 1px;
  padding: 0 8px 12px;
  overflow-y: auto;
}

/* The same row as a session's: glyph slot, title, and a word or a time at the end. */
.automation-row {
  display: flex;
  width: 100%;
  min-width: 0;
  min-height: 32px;
  align-items: center;
  gap: 9px;
  border: 0;
  border-radius: var(--radius-btn);
  padding: 0 10px;
  background: transparent;
  color: color-mix(in srgb, var(--text) 86%, transparent);
  text-align: left;
  cursor: pointer;
  transition: background var(--transition), color var(--transition);
}

.automation-row:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.automation-row--active {
  background: color-mix(in srgb, var(--text) 9%, transparent);
  color: var(--text);
  font-weight: 500;
}

.automation-row:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

/* An automation that's off fades back, as a quiet session does. */
.automation-row--off:not(.automation-row--active) {
  color: color-mix(in srgb, var(--text) 45%, transparent);
}

.automation-row__glyph-slot {
  width: 8px;
  height: 8px;
  flex-shrink: 0;
}

.automation-row__draft-icon {
  width: 11px;
  height: 11px;
  flex-shrink: 0;
  color: var(--muted);
}

.automation-row__title {
  flex: 1 1 auto;
  min-width: 0;
  overflow: hidden;
  font-size: 13px;
  line-height: 1.3;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.automation-row__meta {
  flex-shrink: 0;
  color: color-mix(in srgb, var(--muted) 80%, transparent);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
  font-weight: 400;
  line-height: 1.3;
  white-space: nowrap;
}

.automation-row__meta--working {
  color: var(--running);
}

.automation-row__meta--error {
  color: var(--error);
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
