<script setup lang="ts">
import { computed } from "vue";
import { MessageSquare } from "lucide-vue-next";
import type { SessionListItem } from "@/api/client";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import { useRelativeTime } from "@/composables/use-relative-time";
import { isSessionLive, sessionRowStatus } from "@/lib/session-row-status";

/** The Fleet session working on a pull request or issue, with its live status; a click opens it. */
const props = defineProps<{
  sessions: SessionListItem[];
}>();

const emit = defineEmits<{
  open: [session: SessionListItem];
}>();

const now = useRelativeTime();
const first = computed(() => props.sessions[0]);
const status = computed(() => (first.value ? sessionRowStatus(first.value, now.value) : null));
const words = computed(() => {
  const item = first.value;
  if (!item || !status.value) return "";
  if (item.sessionStatus === "waiting_input") return "Needs you";
  if (status.value.tone === "working") return status.value.description;
  return "Idle";
});
const title = computed(() => {
  const names = props.sessions.map((item) => item.session.title?.trim() || "Untitled session");
  return `In Fleet: ${names.join(", ")}`;
});
</script>

<template>
  <button
    v-if="first"
    type="button"
    class="gh-session-chip"
    :data-tone="first.sessionStatus === 'waiting_input' ? 'attention' : status?.tone"
    :title="title"
    data-testid="github-session-chip"
    @click.stop="emit('open', first)"
  >
    <StatusGlyph
      v-if="isSessionLive(first)"
      :status="first.sessionStatus"
      :activity="first.activityStatus"
      :label="status?.description"
    />
    <MessageSquare
      v-else
      :size="12"
      aria-hidden="true"
    />
    {{ words }}
    <span
      v-if="sessions.length > 1"
      class="gh-session-chip__more"
    >+{{ sessions.length - 1 }}</span>
  </button>
</template>

<style scoped>
.gh-session-chip {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  flex-shrink: 0;
  height: 22px;
  padding: 0 8px;
  border: 0;
  border-radius: 6px;
  background: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--muted);
  font-size: 11.5px;
  white-space: nowrap;
  cursor: pointer;
  transition: background-color var(--transition), color var(--transition);
}

.gh-session-chip:hover {
  color: var(--text);
}

.gh-session-chip[data-tone="working"] {
  background: var(--accent-dim);
  color: var(--text);
}

.gh-session-chip[data-tone="attention"] {
  background: color-mix(in srgb, var(--status-waiting) 14%, transparent);
  color: var(--status-waiting);
}

.gh-session-chip:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.gh-session-chip__more {
  color: var(--muted);
  font-variant-numeric: tabular-nums;
}
</style>
