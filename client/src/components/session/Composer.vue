<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, shallowRef, ref, useTemplateRef, watch } from "vue";
import { storeToRefs } from "pinia";
import { Send, Paperclip, X } from "lucide-vue-next";
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
import { trackAction } from "@/lib/track-action";
import { useSessionsStore } from "@/stores/sessions";
import type { ImageAttachment } from "@/lib/api-types";
import { ALLOWED_IMAGE_MIMES, MAX_IMAGE_BYTES, MAX_ATTACHMENTS_PER_PROMPT } from "@/lib/image-validation";

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
const { models, defaultModelKey } = useModels(props.sessionId);
const { draft, setText, setAgentId, setModelId } = useDraftState(props.sessionId, {
  agentId: "",
  modelId: "",
});
const { error: sendPromptError, sendPrompt } = useSendPrompt(props.sessionId);
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
let pasteErrorTimer: ReturnType<typeof setTimeout> | null = null;

interface PendingAttachment extends ImageAttachment {
  id: string;
  previewUrl: string;
}

const pendingAttachments = ref<PendingAttachment[]>([]);
const pasteError = shallowRef<string | undefined>(undefined);
const isDragging = shallowRef(false);
const lightboxUrl = shallowRef<string | null>(null);
const fileInputRef = useTemplateRef<HTMLInputElement>("fileInput");

function clearPasteError(): void {
  pasteError.value = undefined;
  if (pasteErrorTimer) {
    clearTimeout(pasteErrorTimer);
    pasteErrorTimer = null;
  }
}

function setPasteError(message: string): void {
  pasteError.value = message;
  if (pasteErrorTimer) {
    clearTimeout(pasteErrorTimer);
  }
  pasteErrorTimer = setTimeout(() => {
    pasteError.value = undefined;
    pasteErrorTimer = null;
  }, 5000);
}

function processImageBlob(blob: File): void {
  if (!ALLOWED_IMAGE_MIMES.has(blob.type)) {
    setPasteError(`Unsupported image type: ${blob.type || "unknown"}`);
    return;
  }
  if (blob.size > MAX_IMAGE_BYTES) {
    setPasteError(`Image exceeds 5MB limit (${(blob.size / 1024 / 1024).toFixed(1)}MB)`);
    return;
  }
  if (pendingAttachments.value.length >= MAX_ATTACHMENTS_PER_PROMPT) {
    setPasteError(`Too many images (max ${MAX_ATTACHMENTS_PER_PROMPT})`);
    return;
  }

  const reader = new FileReader();
  reader.onload = () => {
    const dataUrl = reader.result as string;
    const base64 = dataUrl.split(",")[1] ?? "";
    const previewUrl = URL.createObjectURL(blob);
    pendingAttachments.value.push({
      id: `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
      mime: blob.type,
      filename: blob.name || "image.png",
      data: base64,
      previewUrl,
    });
    clearPasteError();
  };
  reader.readAsDataURL(blob);
}

function removeAttachment(id: string): void {
  const index = pendingAttachments.value.findIndex((a) => a.id === id);
  if (index >= 0) {
    const [removed] = pendingAttachments.value.splice(index, 1);
    URL.revokeObjectURL(removed.previewUrl);
  }
}

function clearAttachments(): void {
  for (const att of pendingAttachments.value) {
    URL.revokeObjectURL(att.previewUrl);
  }
  pendingAttachments.value = [];
}

function handlePaste(event: ClipboardEvent): void {
  const items = Array.from(event.clipboardData?.items ?? []);
  const imageItems = items.filter((item) => item.type.startsWith("image/"));
  if (imageItems.length === 0) return;
  event.preventDefault();
  for (const item of imageItems) {
    const blob = item.getAsFile();
    if (blob) processImageBlob(blob);
  }
}

function handleDragOver(event: DragEvent): void {
  event.preventDefault();
  isDragging.value = true;
}

function handleDragLeave(): void {
  isDragging.value = false;
}

function handleDrop(event: DragEvent): void {
  event.preventDefault();
  isDragging.value = false;
  const files = Array.from(event.dataTransfer?.files ?? []);
  for (const file of files) {
    if (file.type.startsWith("image/")) {
      processImageBlob(file);
    }
  }
}

function handleFileInput(event: Event): void {
  const input = event.target as HTMLInputElement;
  const files = Array.from(input.files ?? []);
  for (const file of files) {
    processImageBlob(file);
  }
  input.value = "";
}

const hasContent = computed(() => draft.text.trim().length > 0 || pendingAttachments.value.length > 0);

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
  clearAttachments();
  clearPasteError();
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
  [models, defaultModelKey],
  ([nextModels, nextDefaultModelKey]) => {
    if (!nextDefaultModelKey) {
      return;
    }

    if (draft.modelId && !nextModels.some((model) => model.selectionKey === draft.modelId)) {
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

  const attachments: ImageAttachment[] = pendingAttachments.value.map(({ mime, filename, data }) => ({ mime, filename, data }));
  clearAttachments();
  return sendPrompt(attachments.length > 0 ? attachments : undefined);
}

function handleSend(): void {
  if (isDisabled.value) {
    return;
  }

  const text = draft.text.trim();
  if (!text && pendingAttachments.value.length === 0) {
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
  trackAction("session.prompt", props.sessionId);

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
      v-if="sendError || pasteError"
      data-testid="send-prompt-error"
      class="composer-error"
      role="alert"
    >
      {{ sendError || pasteError }}
    </div>

    <div
      class="composer-box"
      :class="{ 'composer-box--dragging': isDragging }"
      @dragover="handleDragOver"
      @dragleave="handleDragLeave"
      @drop="handleDrop"
    >
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
        @paste="handlePaste"
      />

      <div
        v-if="pendingAttachments.length > 0"
        class="attachment-strip"
      >
        <div
          v-for="att in pendingAttachments"
          :key="att.id"
          class="attachment-chip"
        >
          <button
            type="button"
            class="attachment-chip__thumb-btn"
            title="Click to preview"
            @click="lightboxUrl = att.previewUrl"
          >
            <img
              :src="att.previewUrl"
              :alt="att.filename ?? 'image'"
              class="attachment-chip__thumb"
            >
          </button>
          <span class="attachment-chip__name">{{ att.filename ?? 'image.png' }}</span>
          <button
            type="button"
            class="attachment-chip__remove"
            :title="`Remove ${att.filename ?? 'image'}`"
            @click="removeAttachment(att.id)"
          >
            <X class="attachment-chip__remove-icon" />
          </button>
        </div>
      </div>

      <input
        ref="fileInput"
        type="file"
        accept="image/png,image/jpeg,image/gif,image/webp"
        multiple
        class="sr-only"
        @change="handleFileInput"
      >

      <div class="composer-toolbar">
        <button
          type="button"
          class="attach-btn"
          title="Attach image"
          :disabled="isDisabled"
          @click="fileInputRef?.click()"
        >
          <Paperclip class="attach-btn__icon" />
        </button>
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
          :disabled="isDisabled || !hasContent"
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

    <Teleport to="body">
      <div
        v-if="lightboxUrl"
        class="lightbox-overlay"
        @click="lightboxUrl = null"
      >
        <img
          :src="lightboxUrl"
          alt="Image preview"
          class="lightbox-image"
          @click.stop
        >
        <button
          type="button"
          class="lightbox-close"
          title="Close preview"
          @click="lightboxUrl = null"
        >
          <X class="lightbox-close__icon" />
        </button>
      </div>
    </Teleport>
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
  font-size: 11px;
  line-height: 1.4;
}

.composer-error {
  margin: 0 0 10px;
  border: 1px solid color-mix(in srgb, var(--error) 30%, transparent);
  border-radius: 10px;
  padding: 10px 12px;
  background: color-mix(in srgb, var(--error) 10%, transparent);
  color: var(--error);
  font-size: 11px;
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
  font-size: 12px;
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
  font-size: 11px;
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
  font-size: 10px;
  color: var(--muted);
  white-space: nowrap;
}

.composer-box--dragging {
  border-color: var(--accent);
  background: color-mix(in srgb, var(--accent) 5%, var(--card-bg));
}

.attachment-strip {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  padding: 8px 12px 4px;
}

.attachment-chip {
  display: inline-flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
  padding: 6px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--panel-bg);
  font-size: 10px;
  color: var(--text);
  position: relative;
}

.attachment-chip__thumb-btn {
  display: inline-flex;
  padding: 0;
  border: none;
  background: transparent;
  cursor: pointer;
  border-radius: 4px;
}

.attachment-chip__thumb-btn:hover {
  opacity: 0.8;
}

.attachment-chip__thumb {
  width: 80px;
  height: 80px;
  border-radius: 4px;
  object-fit: cover;
}

.attachment-chip__name {
  max-width: 80px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--muted);
  font-size: 10px;
}

.attachment-chip__remove {
  position: absolute;
  top: 4px;
  right: 4px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 18px;
  height: 18px;
  padding: 0;
  border: none;
  border-radius: 50%;
  background: rgba(0, 0, 0, 0.6);
  color: #fff;
  cursor: pointer;
  opacity: 0;
  transition: opacity 0.15s;
}

.attachment-chip:hover .attachment-chip__remove {
  opacity: 1;
}

.attachment-chip__remove:hover {
  background: rgba(220, 38, 38, 0.8);
}

.attachment-chip__remove-icon {
  width: 12px;
  height: 12px;
}

.attach-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  padding: 0;
  border: none;
  border-radius: 8px;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
}

.attach-btn:hover {
  background: color-mix(in srgb, var(--text) 8%, transparent);
  color: var(--text);
}

.attach-btn:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}

.attach-btn__icon {
  width: 15px;
  height: 15px;
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
  border: 0;
}
</style>

<style>
.lightbox-overlay {
  position: fixed;
  inset: 0;
  z-index: 9999;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgba(0, 0, 0, 0.8);
  cursor: pointer;
}

.lightbox-image {
  max-width: 90vw;
  max-height: 90vh;
  border-radius: 8px;
  object-fit: contain;
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.5);
  cursor: default;
}

.lightbox-close {
  position: absolute;
  top: 16px;
  right: 16px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 36px;
  height: 36px;
  padding: 0;
  border: none;
  border-radius: 50%;
  background: rgba(255, 255, 255, 0.15);
  color: #fff;
  cursor: pointer;
}

.lightbox-close:hover {
  background: rgba(255, 255, 255, 0.25);
}

.lightbox-close__icon {
  width: 20px;
  height: 20px;
}
</style>
