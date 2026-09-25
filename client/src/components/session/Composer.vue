<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, shallowRef, ref, useTemplateRef, watch } from "vue";
import { storeToRefs } from "pinia";
import { ArrowUp, Paperclip, SquareTerminal, X, CircleX } from "lucide-vue-next";
import AutocompletePopup from "@/components/session/AutocompletePopup.vue";
import ComposerFrame from "@/components/session/ComposerFrame.vue";
import ImageLightbox from "@/components/session/ImageLightbox.vue";
import AgentSelector from "@/components/session/AgentSelector.vue";
import ModelSelector from "@/components/session/ModelSelector.vue";
import EffortToggle from "@/components/session/EffortToggle.vue";
import { Button } from "@/components/ui/button";
import { useAgents } from "@/composables/use-agents";
import { useAbortSession } from "@/composables/use-session-actions";
import { useAutocomplete } from "@/composables/use-autocomplete";
import { useDraftState } from "@/composables/use-draft-state";
import { useInputHistory } from "@/composables/use-input-history";
import { useMessageQueue } from "@/composables/use-message-queue";
import { useIsMobile } from "@/composables/use-media-query";
import { useSendCommand } from "@/composables/use-send-command";
import { useModels } from "@/composables/use-models";
import { useDraftAttachments } from "@/composables/use-draft-attachments";
import { useDraftTerminalContext } from "@/composables/use-draft-terminal-context";
import { describeSessionDefaults } from "@/lib/agent-model-choice";
import { formatTerminalContext, terminalLineRange } from "@/lib/format-terminal-context";
import { splitDraftReferences } from "@/lib/composer-references";
import { useSendPrompt } from "@/composables/use-send-prompt";
import { parseSlashCommand } from "@/lib/slash-command-utils";
import { trackAction } from "@/lib/track-action";
import { useSessionsStore } from "@/stores/sessions";
import type { ImageAttachment } from "@/lib/client-types";
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

const { agents, defaultAgentId } = useAgents(props.sessionId);
const { abortSession, isAborting } = useAbortSession();
const { models, defaultModelKey, modelsByKey } = useModels(props.sessionId);
const { draft, setText, setAgentId, setModelId, setEffort } = useDraftState(props.sessionId, {
  agentId: "",
  modelId: "",
});
const { canSend, error: sendPromptError, sendPrompt } = useSendPrompt(props.sessionId);
const { error: sendCommandError, sendCommand } = useSendCommand(props.sessionId);
const inputHistory = useInputHistory(props.sessionId);
const historyEl = ref<HTMLElement | null>(null);

const sessionsStore = useSessionsStore();
const isMobile = useIsMobile();
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

const { attachments: pendingAttachments, addAttachment, removeAttachment: removeDraftAttachment, clearAttachments } = useDraftAttachments(props.sessionId);
const {
  contexts: terminalContexts,
  removeContext: removeTerminalContext,
  clearContexts: clearTerminalContexts,
} = useDraftTerminalContext(props.sessionId);
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
    addAttachment({
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
  removeDraftAttachment(id);
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

const hasContent = computed(() =>
  draft.text.trim().length > 0 || pendingAttachments.value.length > 0 || terminalContexts.value.length > 0);

const STATUS_INDICATOR_LINGER_MS = 1600;
const STATUS_INDICATOR_DOTS_INTERVAL_MS = 400;

const selectedSession = computed(() => {
  return sessions.value.find((session) => session.session.id === props.sessionId) ?? null;
});

const sessionStateOverride = computed(() => {
  return sessionStateOverrides.value[props.sessionId] ?? null;
});

const effectiveActivityStatus = computed(() => {
  return sessionStateOverride.value?.activityStatus ?? selectedSession.value?.activityStatus;
});

const isDisabled = computed(() => {
  const retentionStatus = sessionStateOverride.value?.retentionStatus ?? selectedSession.value?.retentionStatus;

  return props.disabled
    || localDisabledOverride.value === true
    || retentionStatus === "archived"
    || !canSend.value;
});

const sessionStatus = computed<"idle" | "busy" | "waiting_input">(() => {
  if (optimisticBusy.value) {
    return "busy";
  }

  const activity = effectiveActivityStatus.value;
  if (activity === "busy") return "busy";
  if (activity === "delegating") return "busy";
  if (activity === "retry") return "busy";
  if (activity === "waiting_input") return "waiting_input";
  return "idle";
});

const canInterrupt = computed(() => sessionStatus.value === "busy" && !isAborting.value);

async function handleInterrupt(): Promise<void> {
  if (!canInterrupt.value || !props.instanceId) return;
  try {
    await abortSession(props.sessionId);
    sessionsStore.patchSession(props.sessionId, { activityStatus: "idle", sessionStatus: "idle" });
  } catch {
    // Errors are handled by the mutation composable state.
  }
}

watch(
  effectiveActivityStatus,
  (activityStatus) => {
    if (activityStatus === "busy" || activityStatus === "delegating" || activityStatus === "retry") {
      optimisticBusy.value = false;
    }
  },
);

watch(
  () => [selectedSession.value?.retentionStatus, props.disabled, canSend.value] as const,
  ([retentionStatus, disabled, nextCanSend]) => {
    if (disabled || retentionStatus === "archived" || !nextCanSend) {
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
    if (retentionStatus === "archived") {
      localDisabledOverride.value = true;
      return;
    }

    if (retentionStatus === "active" && canSend.value) {
      localDisabledOverride.value = null;
    }
  });
}

function syncDisabledStateFromPage(): void {
  if (typeof document === "undefined") {
    return;
  }

  const hasArchivedBanner = document.querySelector('[data-testid="session-archived-banner"]') !== null;
  if (hasArchivedBanner) {
    localDisabledOverride.value = true;
    return;
  }

  if (!props.disabled && canSend.value) {
    localDisabledOverride.value = null;
  }
}

onMounted(() => {
  syncDisabledStateFromPage();

  if (typeof ResizeObserver !== "undefined" && textareaRef.value) {
    mirrorResizeObserver = new ResizeObserver(() => syncMirror());
    mirrorResizeObserver.observe(textareaRef.value);
  }

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
  mirrorResizeObserver?.disconnect();
  mirrorResizeObserver = null;
  clearStatusIndicatorTimer();
  stopStatusIndicatorDots();
  clearPasteError();
});

const { queue, enqueue } = useMessageQueue(
  computed<"idle" | "busy">(() => sessionStatus.value === "idle" ? "idle" : "busy"),
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
const mirrorRef = useTemplateRef<HTMLDivElement>("mirror");
let mirrorResizeObserver: ResizeObserver | null = null;

/**
 * The mirror sits behind the (transparent) text area with the same text laid out the same way, so
 * `@` references can be tinted where they sit. Keep its height, line width and scroll in step.
 */
function syncMirror(): void {
  const textarea = textareaRef.value;
  const mirror = mirrorRef.value;
  if (!textarea || !mirror) {
    return;
  }

  mirror.style.height = `${textarea.offsetHeight}px`;
  // A scrollbar narrows the text area's lines; narrow the mirror's to match.
  const scrollbarWidth = textarea.offsetWidth - textarea.clientWidth;
  mirror.style.paddingRight = `${parseFloat(getComputedStyle(textarea).paddingRight) + scrollbarWidth}px`;
  mirror.scrollTop = textarea.scrollTop;
}

function focusPrompt(): void {
  textareaRef.value?.focus();
}

defineExpose({
  focusPrompt,
});

const cursorPosition = shallowRef(0);
const hasValidSessionId = computed(() => Boolean(props.sessionId?.trim()));
const autocomplete = useAutocomplete({
  value: computed(() => draft.text),
  setValue: setText,
  sessionId: computed(() => props.sessionId),
  inputRef: textareaRef,
  cursorPosition,
});
const draftSegments = computed(() => splitDraftReferences(draft.text, cursorPosition.value));

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

/** "Default" is the session's own agent and model, which prompts that name none get; the chips say which. */
const defaultLabels = computed(() => {
  const session = sessions.value.find((candidate) => candidate.session.id === props.sessionId);
  return describeSessionDefaults({
    sessionAgent: session?.selectedAgent,
    sessionModel: session?.selectedModel,
    draftAgent: draft.agentId,
    defaultAgent: defaultAgentId.value,
    agents: agents.value,
    models: models.value,
  });
});

const selectedEffort = computed({
  get: () => draft.effort,
  set: (value) => {
    setEffort(value);
  },
});

const selectedModelVariants = computed(() => {
  const modelId = selectedModelId.value || defaultModelKey.value || "";
  const model = modelsByKey.value[modelId];
  return model?.variants ?? [];
});

const supportsReasoning = computed(() => {
  return selectedModelVariants.value.length > 0;
});

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

    if (nextStatus === "busy" || nextStatus === "waiting_input") {
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
  syncMirror();
}

watch(
  () => draft.text,
  async () => {
    await nextTick();
    resizeTextarea();
  },
  { immediate: true },
);

// A model picked in an earlier visit that the session no longer offers goes back to "Default" when the list first
// loads. Once it's up, a list the harness changed doesn't change the pick: the chip names the model it had.
let draftModelChecked = false;
watch(
  [models, defaultModelKey],
  ([nextModels, nextDefaultModelKey]) => {
    if (!nextDefaultModelKey || draftModelChecked) {
      return;
    }

    draftModelChecked = true;
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

  // Terminal lines go in front of what was typed, as fenced blocks. If the send is refused, the draft is put back.
  const typed = draft.text;
  const withTerminalLines = terminalContexts.value.length > 0;
  if (withTerminalLines) setText(formatTerminalContext(terminalContexts.value, typed));

  const attachments: ImageAttachment[] = pendingAttachments.value.map(({ mime, filename, data }) => ({ mime, filename, data }));
  clearAttachments();
  const sent = sendPrompt(attachments.length > 0 ? attachments : undefined);
  if (withTerminalLines) {
    if (sent) clearTerminalContexts();
    else setText(typed);
  }
  return sent;
}

function handleSend(): void {
  if (isDisabled.value) {
    return;
  }

  const text = draft.text.trim();
  if (!text && pendingAttachments.value.length === 0 && terminalContexts.value.length === 0) {
    return;
  }

  inputHistory.push(text);

  if (sessionStatus.value === "busy") {
    enqueue(formatTerminalContext(terminalContexts.value, text));
    clearTerminalContexts();
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

  // --- Input history popup handling ---
  if (inputHistory.isOpen.value) {
    if (event.key === "ArrowUp") {
      event.preventDefault();
      inputHistory.moveDown();
      void nextTick(() => { historyEl.value?.querySelector('.input-history__item--selected')?.scrollIntoView({ block: 'nearest' }); });
      return;
    }
    if (event.key === "ArrowDown") {
      event.preventDefault();
      inputHistory.moveUp();
      void nextTick(() => { historyEl.value?.querySelector('.input-history__item--selected')?.scrollIntoView({ block: 'nearest' }); });
      return;
    }
    if (event.key === "Enter") {
      event.preventDefault();
      const selected = inputHistory.confirm();
      if (selected !== undefined) {
        setText(selected);
        void nextTick(() => resizeTextarea());
      }
      return;
    }
    if (event.key === "Escape") {
      event.preventDefault();
      inputHistory.close();
      return;
    }
    // Any other key closes history and falls through
    inputHistory.close();
  }

  // Open history when pressing ArrowUp in an empty textarea
  if (event.key === "ArrowUp" && !draft.text.trim()) {
    event.preventDefault();
    inputHistory.open();
    void nextTick(() => { if (historyEl.value) historyEl.value.scrollTop = historyEl.value.scrollHeight; });
    return;
  }

  const autocompleteWasOpen = hasValidSessionId.value && autocomplete.isOpen.value;

  if (hasValidSessionId.value) {
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

  // On mobile, Enter inserts a newline; user taps the Send button to submit
  if (isMobile.value) {
    return;
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
      v-if="sendError || pasteError"
      data-testid="send-prompt-error"
      class="composer-error"
      role="alert"
    >
      {{ sendError || pasteError }}
    </div>

    <ComposerFrame
      :dragging="isDragging"
      @dragover="handleDragOver"
      @dragleave="handleDragLeave"
      @drop="handleDrop"
    >
      <AutocompletePopup
        :open="hasValidSessionId && autocomplete.isOpen.value"
        :items="hasValidSessionId ? autocomplete.items.value : []"
        :is-loading="hasValidSessionId ? autocomplete.isLoading.value : false"
        :selected-value="hasValidSessionId ? autocomplete.selectedValue.value : null"
        :error="hasValidSessionId ? autocomplete.error.value : undefined"
        :on-select="autocomplete.onSelect"
        :on-open-folder="autocomplete.onOpenFolder"
      />

      <div
        v-if="inputHistory.isOpen.value"
        ref="historyEl"
        class="input-history"
        role="listbox"
        aria-label="Message history"
      >
        <button
          v-for="(entry, revIdx) in [...inputHistory.entries.value].reverse()"
          :key="revIdx"
          type="button"
          role="option"
          class="input-history__item"
          :class="{ 'input-history__item--selected': (inputHistory.entries.value.length - 1 - revIdx) === inputHistory.selectedIndex.value }"
          :aria-selected="(inputHistory.entries.value.length - 1 - revIdx) === inputHistory.selectedIndex.value"
          @mousedown.prevent="() => { setText(entry); inputHistory.close(); void nextTick(() => resizeTextarea()); }"
          @mouseenter="inputHistory.selectedIndex.value = inputHistory.entries.value.length - 1 - revIdx"
        >
          {{ entry }}
        </button>
      </div>

      <div class="composer-input">
        <!--
          No whitespace between these nodes: the mirror lays text out as the text area does. The
          trailing space gives a draft that ends in a newline its last line, as the text area has.
        -->
        <!-- eslint-disable vue/multiline-html-element-content-newline, vue/singleline-html-element-content-newline -->
        <div
          ref="mirror"
          class="composer-frame__textarea composer-input__mirror"
          data-testid="prompt-references"
          aria-hidden="true"
        ><template
          v-for="(segment, index) in draftSegments"
          :key="index"
        ><span
          v-if="segment.reference"
          class="composer-reference"
        >{{ segment.text }}</span><template v-else>{{ segment.text }}</template></template>{{ " " }}</div>
        <!-- eslint-enable vue/multiline-html-element-content-newline, vue/singleline-html-element-content-newline -->
        <textarea
          ref="textarea"
          class="composer-frame__textarea"
          data-testid="prompt-input"
          :value="draft.text"
          :disabled="isDisabled"
          rows="1"
          placeholder="Type a message…"
          @input="handleInput"
          @keydown="handleKeydown"
          @keyup="handleCursorPositionChange"
          @click="handleCursorPositionChange"
          @scroll="syncMirror"
          @paste="handlePaste"
        />
      </div>

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

      <div
        v-if="terminalContexts.length > 0"
        class="terminal-context-strip"
        aria-label="Terminal lines in this message"
      >
        <span
          v-for="context in terminalContexts"
          :key="context.id"
          class="terminal-context-chip"
          :title="context.text"
        >
          <SquareTerminal
            class="terminal-context-chip__icon"
            aria-hidden="true"
          />
          <span>{{ context.label }}</span>
          <span class="terminal-context-chip__range">{{ terminalLineRange(context.from, context.to) }}</span>
          <button
            type="button"
            class="terminal-context-chip__remove"
            :aria-label="`Remove ${context.label} ${terminalLineRange(context.from, context.to)}`"
            @click="removeTerminalContext(context.id)"
          >
            <X
              class="terminal-context-chip__remove-icon"
              aria-hidden="true"
            />
          </button>
        </span>
      </div>

      <input
        ref="fileInput"
        type="file"
        accept="image/png,image/jpeg,image/gif,image/webp"
        multiple
        class="sr-only"
        @change="handleFileInput"
      >

      <template #toolbar>
        <Button
          variant="toolbar-icon"
          size="toolbar"
          title="Attach image"
          :disabled="isDisabled"
          @click="fileInputRef?.click()"
        >
          <Paperclip class="size-3.5" />
        </Button>
        <AgentSelector
          v-model="selectedAgentId"
          :agents="agents"
          :default-label="defaultLabels.agentLabel"
          :default-description="defaultLabels.agentDescription"
        />
        <ModelSelector
          v-model="selectedModelId"
          :models="models"
          :default-label="defaultLabels.modelLabel"
          :default-description="defaultLabels.modelDescription"
        />
        <EffortToggle
          v-if="supportsReasoning"
          v-model="selectedEffort"
          :variants="selectedModelVariants"
        />
        <Button
          variant="toolbar-icon-danger"
          size="toolbar"
          title="Interrupt"
          :disabled="!canInterrupt"
          @click="handleInterrupt"
        >
          <CircleX class="size-3.5" />
        </Button>

        <Button
          variant="default"
          size="toolbar-lg"
          class="composer-frame__send"
          data-testid="prompt-send-button"
          aria-label="Send"
          title="Send"
          :disabled="isDisabled || !hasContent"
          @click="handleSend"
        >
          <ArrowUp class="size-4" />
        </Button>
        <span
          v-if="queue.length > 0"
          class="queue-badge"
          :title="`${queue.length} message(s) queued`"
        >
          {{ queue.length }} queued
        </span>
      </template>
    </ComposerFrame>

    <ImageLightbox
      :src="lightboxUrl"
      @close="lightboxUrl = null"
    />
  </section>
</template>

<style scoped>
/* The composer floats on the sheet, aligned with the reading column. */
.composer {
  flex-shrink: 0;
  padding: 4px 24px 18px;
  background: transparent;
}

.composer-error {
  max-width: 760px;
  margin: 0 auto 10px;
  border: 1px solid color-mix(in srgb, var(--error) 30%, transparent);
  border-radius: var(--radius-card);
  padding: 10px 12px;
  background: color-mix(in srgb, var(--error) 10%, transparent);
  color: var(--error);
  font-size: 12px;
  line-height: 1.5;
}

.composer-input {
  position: relative;
}

/* Above the mirror, which only paints the reference tints behind the text. */
.composer-input > textarea {
  position: relative;
}

.composer-input__mirror {
  position: absolute;
  top: 0;
  right: 0;
  left: 0;
  min-height: 0;
  max-height: none;
  overflow: hidden;
  color: transparent;
  white-space: pre-wrap;
  overflow-wrap: break-word;
  pointer-events: none;
  user-select: none;
}

.composer-reference {
  --reference-tint: color-mix(in srgb, var(--accent) 28%, transparent);

  border-radius: 4px;
  background: var(--reference-tint);
  /* Widen the tint past the text without moving it. */
  box-shadow: -3px 0 0 var(--reference-tint), 3px 0 0 var(--reference-tint);
  -webkit-box-decoration-break: clone;
  box-decoration-break: clone;
}

.input-history {
  position: absolute;
  bottom: 100%;
  left: 0;
  right: 0;
  max-height: 260px;
  overflow-y: auto;
  border: 1px solid var(--border);
  border-bottom: none;
  border-radius: calc(var(--radius-panel) + 2px) calc(var(--radius-panel) + 2px) 0 0;
  background: var(--card-bg);
  z-index: 20;
  box-shadow: 0 -4px 16px rgba(0, 0, 0, 0.25);
}

.input-history__item {
  display: block;
  width: 100%;
  padding: 8px 16px;
  border: none;
  background: transparent;
  color: var(--text);
  font-size: 12px;
  line-height: 1.4;
  text-align: left;
  cursor: pointer;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.input-history__item--selected {
  background: color-mix(in srgb, var(--accent) 18%, transparent);
}

.input-history__item:hover {
  background: color-mix(in srgb, var(--accent) 12%, transparent);
}

.queue-badge {
  font-size: 11px;
  color: var(--muted);
  white-space: nowrap;
}

.terminal-context-strip {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 8px 12px 2px;
}

.terminal-context-chip {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 24px;
  padding: 0 4px 0 8px;
  border-radius: calc(var(--radius-btn) - 1px);
  background: var(--accent-dim);
  color: var(--accent);
  font-size: 12px;
  font-weight: 500;
}

.terminal-context-chip__icon {
  width: 12px;
  height: 12px;
}

.terminal-context-chip__range {
  font-family: var(--font-mono-stack);
  font-size: 11px;
  opacity: 0.85;
}

.terminal-context-chip__remove {
  display: inline-grid;
  place-items: center;
  width: 18px;
  height: 18px;
  padding: 0;
  border: none;
  border-radius: 4px;
  background: transparent;
  color: inherit;
  opacity: 0.7;
  cursor: pointer;
  transition: opacity var(--transition), background-color var(--transition);
}

.terminal-context-chip__remove:hover {
  opacity: 1;
  background: color-mix(in srgb, var(--accent) 18%, transparent);
}

.terminal-context-chip__remove:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
  opacity: 1;
}

.terminal-context-chip__remove-icon {
  width: 11px;
  height: 11px;
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
  border-radius: var(--radius-btn);
  background: var(--panel-bg);
  font-size: 11px;
  color: var(--text);
  position: relative;
}

.attachment-chip__thumb-btn {
  display: inline-flex;
  padding: 0;
  border: none;
  background: transparent;
  cursor: pointer;
  border-radius: calc(var(--radius-btn) - 3px);
}

.attachment-chip__thumb-btn:hover {
  opacity: 0.8;
}

.attachment-chip__thumb {
  width: 80px;
  height: 80px;
  border-radius: calc(var(--radius-btn) - 3px);
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
  transition: opacity var(--transition);
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
