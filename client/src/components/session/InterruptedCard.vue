<script setup lang="ts">
import { computed } from "vue";
import { changedFilesText, type SessionInterruption } from "@/lib/session-interruption";
import { formatRetryClock } from "@/lib/turn-retry";

/**
 * At the end of the conversation of a session a Fleet restart cut off: when, what didn't finish, and Continue (or Ask
 * again when the restart lost a question) and Dismiss.
 */
const props = defineProps<{
  interruption: SessionInterruption;
  busy?: boolean;
  error?: string;
}>();

const emit = defineEmits<{
  continue: [];
  dismiss: [];
}>();

const clock = computed(() => formatRetryClock(props.interruption.at));
</script>

<template>
  <div
    class="interrupted"
    role="status"
    data-testid="interrupted-card"
  >
    <h3 class="interrupted__title">
      <span
        class="interrupted__dot"
        aria-hidden="true"
      />
      Cut off by a restart
    </h3>
    <p
      v-if="interruption.waiting"
      class="interrupted__line"
      data-testid="interrupted-text"
    >
      Fleet restarted while this was waiting for you (since <b>{{ clock }}</b>). <b>The question was lost.</b>
      Fleet doesn't answer for you.
    </p>
    <template v-else>
      <p
        class="interrupted__line"
        data-testid="interrupted-text"
      >
        Fleet restarted while this was working (since <b>{{ clock }}</b>).
      </p>
      <p
        v-if="interruption.lastCall"
        class="interrupted__line"
        data-testid="interrupted-last-call"
      >
        Last call that didn't finish: <b>{{ interruption.lastCall }}</b>
      </p>
      <p
        v-if="interruption.changedFiles !== null && interruption.changedFiles > 0"
        class="interrupted__line"
        data-testid="interrupted-files"
      >
        {{ changedFilesText(interruption.changedFiles) }}, still on disk.
      </p>
    </template>
    <p
      v-if="error"
      class="interrupted__error"
    >
      {{ error }}
    </p>
    <div class="interrupted__actions">
      <button
        class="interrupted__button interrupted__button--primary"
        type="button"
        data-testid="interrupted-continue"
        :disabled="busy"
        @click="emit('continue')"
      >
        {{ interruption.waiting ? "Ask again" : "Continue" }}
      </button>
      <button
        class="interrupted__button"
        type="button"
        data-testid="interrupted-dismiss"
        :disabled="busy"
        @click="emit('dismiss')"
      >
        Dismiss
      </button>
    </div>
  </div>
</template>

<style scoped>
.interrupted {
  margin-top: 10px;
  padding: 11px 12px;
  border: 1px solid color-mix(in srgb, var(--status-waiting) 40%, var(--border));
  border-radius: var(--radius-card);
  background: color-mix(in srgb, var(--status-waiting) 9%, var(--panel-bg));
}

.interrupted__title {
  display: flex;
  align-items: center;
  gap: 7px;
  margin: 0 0 3px;
  font-size: 13px;
  font-weight: 600;
}

.interrupted__dot {
  width: 9px;
  height: 9px;
  flex: none;
  border-radius: 50%;
  background: var(--status-waiting);
}

.interrupted__line {
  margin: 3px 0;
  color: var(--muted);
  font-size: 12.5px;
  overflow-wrap: anywhere;
}

.interrupted__line b {
  color: var(--text);
  font-weight: 500;
}

.interrupted__error {
  margin: 3px 0;
  color: var(--error);
  font-size: 12px;
}

.interrupted__actions {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 9px;
}

.interrupted__button {
  padding: 5px 12px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--panel-bg);
  color: var(--text);
  font-size: 12.5px;
  cursor: pointer;
  transition: var(--transition);
}

.interrupted__button:hover:not(:disabled) {
  border-color: var(--accent);
  color: var(--accent);
}

.interrupted__button--primary {
  border-color: var(--accent);
  background: var(--accent);
  color: var(--primary-foreground);
}

.interrupted__button--primary:hover:not(:disabled) {
  color: var(--primary-foreground);
  opacity: 0.9;
}

.interrupted__button:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
</style>
