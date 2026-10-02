<script setup lang="ts">
import { computed } from "vue";
import { AlertCircle, Check, Globe } from "lucide-vue-next";
import ToolScreenshot from "@/components/session/ToolScreenshot.vue";
import { apiUrl } from "@/lib/api-client";
import { useAgentBrowserStore } from "@/stores/agent-browser";

/**
 * The steps a tool call took in the agent's own browser, one line each: what happened in plain words, then the
 * operation and how long it took. Fleet ran each one, so it knows them whatever the call's script said; a screenshot
 * step shows its picture. OpenCode 2's Code Mode `execute` can take many; OpenCode's fleet_browser_* tools one each.
 */
const props = defineProps<{
  sessionId: string;
  callId: string | undefined;
  /** Whether the call is still running, so its latest step is the one under way. */
  running?: boolean;
}>();

const store = useAgentBrowserStore();
const steps = computed(() => store.stepsOf(props.sessionId, props.callId));
</script>

<template>
  <div
    v-if="steps.length > 0"
    class="browser-steps"
    data-testid="browser-steps"
  >
    <div class="browser-steps__head">
      <Globe
        :size="12"
        aria-hidden="true"
      />
      <span>{{ running ? "Using its browser" : "Used its browser" }} · {{ steps.length }} step{{ steps.length === 1 ? "" : "s" }}</span>
    </div>
    <ol class="browser-steps__list">
      <li
        v-for="step in steps"
        :key="step.seq"
        class="browser-steps__step"
        :class="{ 'browser-steps__step--failed': !step.ok }"
        data-testid="browser-step"
      >
        <span
          class="browser-steps__icon"
          aria-hidden="true"
        >
          <Check
            v-if="step.ok"
            :size="12"
          />
          <AlertCircle
            v-else
            :size="12"
          />
        </span>
        <span class="browser-steps__summary">
          {{ step.summary }}
          <span
            v-if="!step.ok && step.error"
            class="browser-steps__error"
          >{{ step.error }}</span>
        </span>
        <span class="browser-steps__detail">{{ step.detail }}</span>
        <ToolScreenshot
          v-if="step.screenshot"
          class="browser-steps__shot"
          :screenshot="{
            url: apiUrl(`/api/sessions/${encodeURIComponent(sessionId)}/screenshots/${encodeURIComponent(step.screenshot.id)}`),
            width: step.screenshot.width,
            height: step.screenshot.height,
          }"
          :title="step.title || 'Agent\'s tab'"
        />
      </li>
    </ol>
  </div>
</template>

<style scoped>
/* Lines up with the call's label above it, like the card's body. */
.browser-steps {
  margin: 0 8px 8px 31px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg, var(--panel-bg));
  overflow: hidden;
}

.browser-steps__head {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 10px;
  font-size: 12px;
  color: var(--muted);
  border-bottom: 1px solid var(--border);
}

.browser-steps__list {
  list-style: none;
  margin: 0;
  padding: 4px;
  display: grid;
  gap: 1px;
}

.browser-steps__step {
  display: grid;
  grid-template-columns: 16px minmax(0, 1fr) auto;
  gap: 2px 8px;
  align-items: start;
  padding: 4px 6px;
  border-radius: 6px;
  font-size: 12.5px;
  color: var(--text);
}

.browser-steps__icon {
  display: grid;
  place-items: center;
  height: 18px;
  color: var(--complete, var(--running));
}

.browser-steps__step--failed .browser-steps__icon {
  color: var(--error);
}

.browser-steps__summary {
  min-width: 0;
  overflow-wrap: anywhere;
  line-height: 18px;
}

.browser-steps__error {
  display: block;
  color: var(--muted);
  font-size: 12px;
  line-height: 1.45;
}

.browser-steps__detail {
  font-family: var(--font-mono, ui-monospace, monospace);
  font-size: 11px;
  color: var(--muted);
  white-space: nowrap;
  line-height: 18px;
}

.browser-steps__shot {
  grid-column: 2 / -1;
  padding: 2px 0 2px !important;
}

@media (max-width: 640px) {
  .browser-steps {
    margin-left: 8px;
  }

  .browser-steps__step {
    grid-template-columns: 16px minmax(0, 1fr);
  }

  .browser-steps__detail {
    grid-column: 2;
    white-space: normal;
  }
}
</style>
