<script setup lang="ts">
import { computed } from "vue";
import { PencilLine } from "lucide-vue-next";
import type { NewSessionDraftRow } from "@/stores/workspace-ui";

const props = defineProps<{
  draft: NewSessionDraftRow;
  active: boolean;
}>();

const emit = defineEmits<{
  open: [];
}>();

const label = computed(() => (props.draft.isStarting ? "Starting…" : "Draft"));
</script>

<template>
  <!-- Same box as SessionItem's row, so the session it becomes takes its place exactly. -->
  <div class="draft-row-shell">
    <button
      type="button"
      class="draft-row"
      :class="{ active }"
      data-testid="new-session-draft-row"
      :aria-current="active ? 'true' : undefined"
      @click="emit('open')"
    >
      <PencilLine
        class="draft-row__icon"
        aria-hidden="true"
      />
      <span
        class="draft-row__title"
        :class="{ 'draft-row__title--empty': !draft.title }"
      >{{ draft.title || "New session" }}</span>
      <span class="draft-row__meta">{{ label }}</span>
    </button>
  </div>
</template>

<style scoped>
.draft-row-shell {
  width: 100%;
  display: flex;
  align-items: center;
  padding: 1px 0;
}

.draft-row {
  width: 100%;
  min-width: 0;
  min-height: 32px;
  display: flex;
  align-items: center;
  gap: 9px;
  padding: 0 10px;
  cursor: pointer;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: color-mix(in srgb, var(--text) 86%, transparent);
  text-align: left;
  transition: background var(--transition), color var(--transition);
}

.draft-row:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.draft-row.active {
  background: color-mix(in srgb, var(--text) 9%, transparent);
  color: var(--text);
  font-weight: 500;
}

.draft-row:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

/* Sits in the 8px status-glyph column of a session row, a touch larger to read as an icon. */
.draft-row__icon {
  width: 12px;
  height: 12px;
  flex-shrink: 0;
  margin-inline: -2px;
  color: var(--muted);
}

.draft-row__title {
  flex: 1 1 auto;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 13px;
  line-height: 1.3;
}

.draft-row__title--empty {
  color: var(--muted);
}

.draft-row__meta {
  flex-shrink: 0;
  color: color-mix(in srgb, var(--muted) 80%, transparent);
  font-size: 12px;
  font-weight: 400;
  line-height: 1.3;
  white-space: nowrap;
}
</style>
