<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, shallowRef, useTemplateRef, watch } from "vue";
import { storeToRefs } from "pinia";
import { Send } from "lucide-vue-next";
import AutocompletePopup from "@/components/session/AutocompletePopup.vue";
import AgentSelector from "@/components/session/AgentSelector.vue";
import ModelSelector from "@/components/session/ModelSelector.vue";
import { useAgents } from "@/composables/use-agents";
import { useAutocomplete } from "@/composables/use-autocomplete";
import { useDraftState } from "@/composables/use-draft-state";
import { useMessageQueue } from "@/composables/use-message-queue";
import { useSendCommand } from "@/composables/use-send-command";
import { useModels } from "@/composables/use-models";
import { useSendPrompt } from "@/composables/use-send-prompt";
import { parseSlashCommand } from "@/lib/slash-command-utils";
import { useSessionsStore } from "@/stores/sessions";

defineOptions({
  name: "SessionComposer",
});

const props = defineProps<{
  sessionId: string;
  instanceId?: string;
  disabled?: boolean;
}>();

const emit = defineEmits<{
  promptSent: [];
}>();

const { agents, defaultAgentId } = useAgents();
const { models, defaultModelId } = useModels(props.sessionId);
const { draft, setText, setAgentId, setModelId } = useDraftState(props.sessionId, {
  agentId: "",
  modelId: "",
});
const { canSend, error: sendPromptError, sendPrompt } = useSendPrompt(props.sessionId);
const { error: sendCommandError, sendCommand } = useSendCommand(props.sessionId);

const sessionsStore = useSessionsStore();
const { sessions, sessionStateOverrides } = storeToRefs(sessionsStore);
const optimisticBusy = shallowRef(false);
const localDisabledOverride = shallowRef<boolean | null>(null);
const statusIndicatorVisible = shallowRef(false);
const statusIndicatorPhase = shallowRef<"thinking" | "responding">("thinking");
const statusIndicatorDotCount = shallowRef(1);
const sendError = computed(() => sendCommandError.value ?? sendPromptError.value);
let disabledStateObserver: MutationObserver | null = null;
let statusIndicatorTimer: ReturnType<typeof setTimeout> | null = null;
let statusIndicatorDotsTimer: ReturnType<typeof setInterval> | null = null;

const STATUS_INDICATOR_LINGER_MS = 1600;
const STATUS_INDICATOR_DOTS_INTERVAL_MS = 400;

const selectedSession = computed(() => {
  return sessions.value.find((session) => session.session.id === props.sessionId) ?? null;
});

const sessionStateOverride = computed(() => {
  return sessionStateOverrides.value[props.sessionId] ?? null;
});

const isDisabled = computed(() => {
  const lifecycleStatus = sessionStateOverride.value?.lifecycleStatus ?? selectedSession.value?.lifecycleStatus;
  const retentionStatus = sessionStateOverride.value?.retentionStatus ?? selectedSession.value?.retentionStatus;

  return props.disabled
    || localDisabledOverride.value === true
    || retentionStatus === "archived"
    || (lifecycleStatus !== undefined && lifecycleStatus !== "running");
});

const sessionStatus = computed<"idle" | "busy">(() => {
  if (optimisticBusy.value) {
    return "busy";
  }

  const activity = selectedSession.value?.activityStatus;
  return activity === "busy" ? "busy" : "idle";
});

watch(
  () => selectedSession.value?.activityStatus,
  (activityStatus) => {
    if (activityStatus === "busy") {
      optimisticBusy.value = false;
    }
  },
);

watch(
  () => [selectedSession.value?.retentionStatus, selectedSession.value?.lifecycleStatus, props.disabled] as const,
  ([retentionStatus, lifecycleStatus, disabled]) => {
    if (disabled || retentionStatus === "archived" || (lifecycleStatus !== undefined && lifecycleStatus !== "running")) {
      localDisabledOverride.value = true;
      return;
    }

    localDisabledOverride.value = null;
  },
  { immediate: true },
);

if (typeof window !== "undefined") {
  window.addEventListener("weave:session-state-changed", (event: Event) => {
    const customEvent = event as CustomEvent<{ sessionId?: string; patch?: { retentionStatus?: string | null; lifecycleStatus?: string | null } }>;
    if (customEvent.detail?.sessionId !== props.sessionId) {
      return;
    }

    const retentionStatus = customEvent.detail.patch?.retentionStatus;
    const lifecycleStatus = customEvent.detail.patch?.lifecycleStatus;
    if (retentionStatus === "archived" || (lifecycleStatus !== undefined && lifecycleStatus !== "running")) {
      localDisabledOverride.value = true;
      return;
    }

    if (retentionStatus === "active" && lifecycleStatus === "running") {
      localDisabledOverride.value = null;
    }
  });
}

function syncDisabledStateFromPage(): void {
  if (typeof document === "undefined") {
    return;
  }

  const hasArchivedBanner = document.querySelector('[data-testid="session-archived-banner"]') !== null;
  const hasStoppedBanner = document.querySelector('[data-testid="session-stopped-banner"]') !== null;

  if (hasArchivedBanner || hasStoppedBanner) {
    localDisabledOverride.value = true;
    return;
  }

  if (!props.disabled) {
    localDisabledOverride.value = null;
  }
}

onMounted(() => {
  syncDisabledStateFromPage();

  if (typeof document === "undefined") {
    return;
  }

  disabledStateObserver = new MutationObserver(() => {
    syncDisabledStateFromPage();
  });

  disabledStateObserver.observe(document.body, {
    childList: true,
    subtree: true,
    attributes: true,
  });
});

onUnmounted(() => {
  disabledStateObserver?.disconnect();
  disabledStateObserver = null;
  clearStatusIndicatorTimer();
  stopStatusIndicatorDots();
});

const { queue, enqueue } = useMessageQueue(
  sessionStatus,
  async (text) => {
    setText(text);
    await nextTick();
    if (sendCurrentDraft()) {
      optimisticBusy.value = true;
      emit("promptSent");
    }
    void nextTick(() => {
      resizeTextarea();
      textareaRef.value?.focus();
    });
  },
);

const textareaRef = useTemplateRef<HTMLTextAreaElement>("textarea");
const cursorPosition = shallowRef(0);
const normalizedInstanceId = props.instanceId?.trim() ?? "";
const autocompleteEnabled = computed(() => normalizedInstanceId.length > 0);
const autocomplete = useAutocomplete({
  value: computed(() => draft.text),
  setValue: setText,
  instanceId: normalizedInstanceId,
  inputRef: textareaRef,
  cursorPosition,
});

const selectedAgentId = computed({
  get: () => draft.agentId,
  set: (value: string) => {
    setAgentId(value);
  },
});

const selectedModelId = computed({
  get: () => draft.modelId,
  set: (value: string) => {
    setModelId(value);
  },
});

const busyAgentName = computed(() => {
  const selectedAgent = agents.value.find((agent) => agent.id === draft.agentId);
  const fallbackAgent = agents.value.find((agent) => agent.id === defaultAgentId.value);
  return selectedAgent?.name ?? fallbackAgent?.name ?? "Assistant";
});

const busyStatusLabel = computed(() => {
  return statusIndicatorPhase.value === "responding"
    ? `${busyAgentName.value} is responding`
    : `${busyAgentName.value} is thinking`;
});

const busyStatusDots = computed(() => ".".repeat(statusIndicatorDotCount.value));

function clearStatusIndicatorTimer(): void {
  if (statusIndicatorTimer === null) {
    return;
  }

  clearTimeout(statusIndicatorTimer);
  statusIndicatorTimer = null;
}

function stopStatusIndicatorDots(): void {
  if (statusIndicatorDotsTimer !== null) {
    clearInterval(statusIndicatorDotsTimer);
    statusIndicatorDotsTimer = null;
  }

  statusIndicatorDotCount.value = 1;
}

function startStatusIndicatorDots(): void {
  stopStatusIndicatorDots();
  statusIndicatorDotsTimer = setInterval(() => {
    statusIndicatorDotCount.value = statusIndicatorDotCount.value === 3 ? 1 : statusIndicatorDotCount.value + 1;
  }, STATUS_INDICATOR_DOTS_INTERVAL_MS);
}

watch(statusIndicatorVisible, (visible) => {
  if (visible) {
    startStatusIndicatorDots();
    return;
  }

  stopStatusIndicatorDots();
});

watch(
  sessionStatus,
  (nextStatus) => {
    clearStatusIndicatorTimer();

    if (nextStatus === "busy") {
      statusIndicatorVisible.value = true;
      statusIndicatorPhase.value = "thinking";
      return;
    }

    if (!statusIndicatorVisible.value) {
      return;
    }

    statusIndicatorPhase.value = "responding";
    statusIndicatorTimer = setTimeout(() => {
      statusIndicatorVisible.value = false;
      statusIndicatorPhase.value = "thinking";
      statusIndicatorTimer = null;
    }, STATUS_INDICATOR_LINGER_MS);
  },
  { immediate: true },
);

function resizeTextarea(): void {
  const textarea = textareaRef.value;
  if (!textarea) {
    return;
  }

  textarea.style.height = "0px";
  textarea.style.height = `${Math.min(textarea.scrollHeight, 140)}px`;
}

watch(
  () => draft.text,
  async () => {
    await nextTick();
    resizeTextarea();
  },
  { immediate: true },
);

watch(
  [models, defaultModelId],
  ([nextModels, nextDefaultModelId]) => {
    if (!nextDefaultModelId) {
      return;
    }

    if (draft.modelId && !nextModels.some((model) => model.id === draft.modelId)) {
      setModelId("");
    }
  },
  { immediate: true },
);

function handleInput(event: Event): void {
  if (isDisabled.value) {
    return;
  }

  const target = event.target as HTMLTextAreaElement;
  cursorPosition.value = target.selectionStart ?? target.value.length;
  setText(target.value);
  resizeTextarea();
}

function handleCursorPositionChange(event: Event): void {
  const target = event.target as HTMLTextAreaElement;
  cursorPosition.value = target.selectionStart ?? target.value.length;
}

function sendCurrentDraft(): boolean {
  const parsedCommand = parseSlashCommand(draft.text);
  if (parsedCommand) {
    return sendCommand(parsedCommand.command, parsedCommand.args);
  }

  return sendPrompt();
}

function handleSend(): void {
  if (isDisabled.value) {
    return;
  }

  const text = draft.text.trim();
  if (!text) {
    return;
  }

  if (sessionStatus.value === "busy") {
    enqueue(text);
    setText("");
    void nextTick(() => {
      resizeTextarea();
      textareaRef.value?.focus();
    });
    return;
  }

  if (!sendCurrentDraft()) {
    return;
  }

  optimisticBusy.value = true;
  emit("promptSent");

  void nextTick(() => {
    resizeTextarea();
    textareaRef.value?.focus();
  });
}

function handleKeydown(event: KeyboardEvent): void {
  if (isDisabled.value) {
    return;
  }

  const autocompleteWasOpen = autocompleteEnabled.value && autocomplete.isOpen.value;

  if (autocompleteEnabled.value) {
    autocomplete.onKeyDown(event);

    if (autocompleteWasOpen && ["Enter", "Tab", "Escape"].includes(event.key)) {
      return;
    }
  }

  if (event.key !== "Enter") {
    return;
  }

  if (event.shiftKey) {
    return; // Shift+Enter inserts a newline
  }

  event.preventDefault();
  handleSend();
}
</script>

<template>
  <section
    class="composer"
    aria-label="Message composer"
  >
    <div
      v-show="statusIndicatorVisible"
      class="composer-status"
      aria-live="polite"
    >
      <span
        class="composer-status__dot"
        aria-hidden="true"
      />
      <span class="composer-status__label">{{ busyStatusLabel }}</span>
      <span
        class="composer-status__dots"
        aria-hidden="true"
      >{{ busyStatusDots }}</span>
    </div>

    <div
      v-if="sendError"
      data-testid="send-prompt-error"
      class="composer-error"
      role="alert"
    >
      {{ sendError }}
    </div>

    <div class="composer-box">
      <AutocompletePopup
        :open="autocompleteEnabled && autocomplete.isOpen.value"
        :items="autocompleteEnabled ? autocomplete.items.value : []"
        :is-loading="autocompleteEnabled ? autocomplete.isLoading.value : false"
        :selected-value="autocompleteEnabled ? autocomplete.selectedValue.value : null"
        :error="autocompleteEnabled ? autocomplete.error.value : undefined"
        :on-select="autocomplete.onSelect"
      />

      <textarea
        ref="textarea"
        class="composer-box__textarea"
        data-testid="prompt-input"
        :value="draft.text"
        :disabled="isDisabled"
        rows="1"
        placeholder="Type a message…"
        @input="handleInput"
        @keydown="handleKeydown"
        @keyup="handleCursorPositionChange"
        @click="handleCursorPositionChange"
      />

      <div class="composer-toolbar">
        <AgentSelector
          v-model="selectedAgentId"
          :agents="agents"
        />
        <ModelSelector
          v-model="selectedModelId"
          :models="models"
        />

        <button
          type="button"
          class="send-btn"
          data-testid="prompt-send-button"
          :disabled="isDisabled || !canSend"
          @click="handleSend"
        >
          <Send class="send-btn__icon" />
          <span>Send</span>
        </button>
        <span
          v-if="queue.length > 0"
          class="queue-badge"
          :title="`${queue.length} message(s) queued`"
        >
          {{ queue.length }} queued
        </span>
      </div>
    </div>
  </section>
</template>

<style scoped>
.composer {
  flex-shrink: 0;
  border-top: 1px solid var(--border);
  padding: 12px 24px 16px;
  background: var(--panel-bg);
}

.composer-status {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  margin: 0 0 10px;
  color: var(--muted);
  font-size: 12px;
  line-height: 1.4;
}

.composer-error {
  margin: 0 0 10px;
  border: 1px solid color-mix(in srgb, var(--error) 30%, transparent);
  border-radius: 10px;
  padding: 10px 12px;
  background: color-mix(in srgb, var(--error) 10%, transparent);
  color: var(--error);
  font-size: 12px;
  line-height: 1.5;
}

.composer-status__dot {
  width: 7px;
  height: 7px;
  border-radius: 999px;
  background: var(--accent);
  box-shadow: 0 0 0 0 rgba(99, 102, 241, 0.45);
  animation: composer-status-pulse 1.5s ease-out infinite;
}

.composer-status__label {
  color: var(--muted);
}

.composer-status__dots {
  display: inline-block;
  min-width: 20px;
  color: var(--muted);
}

@keyframes composer-status-pulse {
  0% {
    transform: scale(0.96);
    box-shadow: 0 0 0 0 rgba(99, 102, 241, 0.4);
  }

  70% {
    transform: scale(1);
    box-shadow: 0 0 0 8px rgba(99, 102, 241, 0);
  }

  100% {
    transform: scale(0.96);
    box-shadow: 0 0 0 0 rgba(99, 102, 241, 0);
  }
}

.composer-box {
  position: relative;
  border: 1px solid var(--border);
  border-radius: var(--radius-panel);
  background: var(--card-bg);
}

.composer-box:focus-within {
  border-color: var(--accent);
}

.composer-box__textarea {
  width: 100%;
  min-height: 48px;
  max-height: 140px;
  padding: 12px 16px;
  border: none;
  background: transparent;
  color: var(--text);
  resize: none;
  outline: none;
  font-size: 13px;
  line-height: 1.4;
}

.composer-box__textarea::placeholder {
  color: var(--muted);
}

.composer-toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  border-top: 1px solid var(--border);
}

.send-btn {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 32px;
  padding: 0 16px;
  border: none;
  border-radius: 20px;
  background: var(--accent);
  color: #fff;
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
}

.send-btn:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}

.send-btn:focus-visible {
  outline: 2px solid rgba(255, 255, 255, 0.7);
  outline-offset: 2px;
}

.send-btn__icon {
  width: 13px;
  height: 13px;
}

.queue-badge {
  font-size: 11px;
  color: var(--muted);
  white-space: nowrap;
}
</style>
