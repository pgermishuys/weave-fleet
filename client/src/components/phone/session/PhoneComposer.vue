<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, shallowRef, useTemplateRef } from "vue";
import { ArrowUp, CornerDownRight, Plus, Square, X } from "lucide-vue-next";
import PhoneChipSheet, { type ChipOption } from "@/components/phone/session/PhoneChipSheet.vue";
import PhoneFilePickSheet from "@/components/phone/session/PhoneFilePickSheet.vue";
import PhonePlusSheet, { type PlusChoice } from "@/components/phone/session/PhonePlusSheet.vue";
import { useAgents } from "@/composables/use-agents";
import { primaryAction, useComposerActions } from "@/composables/use-composer-actions";
import { useDraftState } from "@/composables/use-draft-state";
import { useModels } from "@/composables/use-models";
import type { ImageAttachment } from "@/lib/client-types";
import { ALLOWED_IMAGE_MIMES, MAX_IMAGE_BYTES } from "@/lib/image-validation";

/**
 * The phone's composer, per the session rules: one round button — Send when idle, Queue while the agent works, Stop
 * while it works and nothing is typed — with Send now beside it when the harness can steer. Queued messages wait above
 * it marked "Next". Agent, model and effort are chips while you type, each a sheet; + adds a photo, the camera, a file,
 * a command or a side question. Typing `@`, `!` or `/btw` still works.
 */
const props = defineProps<{ sessionId: string; machineName: string }>();
const emit = defineEmits<{ (event: "sent"): void; (event: "side"): void }>();

const MAX_PHOTOS = 5;

const actions = useComposerActions(props.sessionId);
const { draft, setText } = actions;
const { setAgentId, setModelId, setEffort } = useDraftState(props.sessionId, { agentId: "", modelId: "" });
const { agents, defaultAgentId } = useAgents(props.sessionId);
const { models, defaultModelKey, modelsByKey } = useModels(props.sessionId);

const textareaRef = useTemplateRef<HTMLTextAreaElement>("textarea");
const photoInput = useTemplateRef<HTMLInputElement>("photoInput");
const cameraInput = useTemplateRef<HTMLInputElement>("cameraInput");
const focused = shallowRef(false);
const plusOpen = shallowRef(false);
const filesOpen = shallowRef(false);
const chip = shallowRef<"agent" | "model" | "effort" | null>(null);
const photos = shallowRef<(ImageAttachment & { url: string })[]>([]);
const photoError = shallowRef<string | null>(null);
const keyboardInset = shallowRef(0);

const hasContent = computed(() => draft.text.trim().length > 0 || photos.value.length > 0);
const primary = computed(() => primaryAction(actions.status.value, hasContent.value));
const offerSendNow = computed(() => actions.caps.value.canSteer && actions.status.value === "busy" && hasContent.value);

const agentId = computed(() => draft.agentId || defaultAgentId.value || "");
const modelKey = computed(() => draft.modelId || defaultModelKey.value || "");
const agentOptions = computed<ChipOption[]>(() => agents.value.map((agent) => ({ id: agent.id, label: agent.name, detail: agent.description })));
const modelOptions = computed<ChipOption[]>(() => models.value.map((model) => ({ id: model.selectionKey, label: model.name, detail: model.provider })));
const effortVariants = computed(() => modelsByKey.value[modelKey.value]?.variants ?? ["low", "medium", "high"]);
const effortOptions = computed<ChipOption[]>(() => effortVariants.value.map((variant) => ({ id: variant, label: variant.charAt(0).toUpperCase() + variant.slice(1) })));
const agentLabel = computed(() => agents.value.find((agent) => agent.id === agentId.value)?.name ?? "Agent");
const modelLabel = computed(() => models.value.find((model) => model.selectionKey === modelKey.value)?.name ?? "Model");
const effortLabel = computed(() => (draft.effort || "medium").replace(/^./, (c) => c.toUpperCase()));

function resize(): void {
  const el = textareaRef.value;
  if (!el) return;
  el.style.height = "auto";
  el.style.height = `${Math.min(el.scrollHeight, 160)}px`;
}

function onInput(event: Event): void {
  setText((event.target as HTMLTextAreaElement).value);
  resize();
  // Queue and Send now appear as you type and narrow the field: measure again once they're in.
  void nextTick(resize);
}

function submit(steer = false): void {
  if (primary.value === "stop" && !steer) {
    void actions.stop();
    return;
  }
  const attachments = photos.value.map(({ mime, filename, data }) => ({ mime, filename, data }));
  const route = actions.submit({ steer, attachments });
  if (route.kind === "empty" && !attachments.length) return;
  if (route.kind === "side") emit("side");
  photos.value = [];
  emit("sent");
  void nextTick(resize);
}

async function readPhoto(file: File): Promise<void> {
  if (!ALLOWED_IMAGE_MIMES.has(file.type)) {
    photoError.value = "That isn't a picture Fleet can send (PNG, JPEG, GIF or WebP).";
    return;
  }
  if (file.size > MAX_IMAGE_BYTES) {
    photoError.value = "That picture is over 5 MB.";
    return;
  }
  const url = await new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
  photos.value = [...photos.value, { mime: file.type, filename: file.name, data: url.slice(url.indexOf(",") + 1), url }];
}

async function onPhotos(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement;
  photoError.value = null;
  const files = [...(input.files ?? [])];
  const room = MAX_PHOTOS - photos.value.length;
  if (files.length > room) photoError.value = `Up to ${MAX_PHOTOS} pictures per message.`;
  for (const file of files.slice(0, Math.max(0, room))) await readPhoto(file);
  input.value = "";
}

function insert(text: string): void {
  const current = draft.text;
  setText(current && !current.endsWith(" ") ? `${current} ${text}` : `${current}${text}`);
  void nextTick(() => {
    resize();
    textareaRef.value?.focus();
  });
}

function onPlus(choice: PlusChoice): void {
  plusOpen.value = false;
  switch (choice) {
    case "photo":
      photoInput.value?.click();
      break;
    case "camera":
      cameraInput.value?.click();
      break;
    case "file":
      filesOpen.value = true;
      break;
    case "command":
      if (!draft.text.startsWith("!")) setText(`!${draft.text}`);
      void nextTick(() => textareaRef.value?.focus());
      break;
    case "side":
      emit("side");
      break;
  }
}

function pick(id: string): void {
  if (chip.value === "agent") setAgentId(id);
  else if (chip.value === "model") setModelId(id);
  else if (chip.value === "effort") setEffort(id);
}

/** Keeps the dock above the iOS keyboard, which doesn't resize the layout viewport. */
function onViewport(): void {
  const viewport = window.visualViewport;
  if (!viewport) return;
  keyboardInset.value = Math.max(0, window.innerHeight - viewport.height - viewport.offsetTop);
}

onMounted(() => {
  window.visualViewport?.addEventListener("resize", onViewport);
  window.visualViewport?.addEventListener("scroll", onViewport);
  void nextTick(resize);
});

onUnmounted(() => {
  window.visualViewport?.removeEventListener("resize", onViewport);
  window.visualViewport?.removeEventListener("scroll", onViewport);
});

defineExpose({ insert, focus: () => textareaRef.value?.focus() });
</script>

<template>
  <div
    class="pc"
    :style="{ paddingBottom: `calc(${keyboardInset}px + env(safe-area-inset-bottom) + 8px)` }"
    data-testid="phone-composer"
  >
    <ol
      v-if="actions.queue.value.length"
      class="pc__queue"
      aria-label="Queued"
    >
      <li
        v-for="(item, index) in actions.queue.value"
        :key="item.id"
        class="pc__queued"
        data-testid="phone-queued"
      >
        <span class="pc__next">{{ index === 0 ? "Next" : "Then" }}</span>
        <span class="pc__queued-text">{{ item.text }}</span>
        <button
          v-if="actions.canSendQueued(item)"
          type="button"
          class="pc__mini"
          @click="actions.sendQueued(item)"
        >
          Send now
        </button>
        <button
          type="button"
          class="pc__mini"
          :aria-label="`Remove ${item.text}`"
          @click="actions.removeQueued(item)"
        >
          <X
            :size="14"
            aria-hidden="true"
          />
        </button>
      </li>
    </ol>

    <div
      v-if="focused || chip"
      class="pc__chips"
    >
      <button
        type="button"
        class="pc__chip"
        data-testid="chip-agent"
        @mousedown.prevent
        @click="chip = 'agent'"
      >
        {{ agentLabel }}
      </button>
      <button
        type="button"
        class="pc__chip"
        data-testid="chip-model"
        @mousedown.prevent
        @click="chip = 'model'"
      >
        {{ modelLabel }}
      </button>
      <button
        type="button"
        class="pc__chip"
        @mousedown.prevent
        @click="chip = 'effort'"
      >
        {{ effortLabel }}
      </button>
    </div>

    <div
      v-if="photos.length"
      class="pc__photos"
    >
      <span
        v-for="(photo, index) in photos"
        :key="index"
        class="pc__photo"
      >
        <img
          :src="photo.url"
          :alt="photo.filename ?? 'Picture'"
        >
        <button
          type="button"
          class="pc__photo-x"
          aria-label="Remove picture"
          @click="photos = photos.filter((_, i) => i !== index)"
        ><X :size="12" /></button>
      </span>
    </div>
    <p
      v-if="photoError || actions.error.value"
      class="pc__error"
      role="alert"
    >
      {{ photoError ?? actions.error.value }}
    </p>

    <form
      class="pc__box"
      @submit.prevent="submit(false)"
    >
      <button
        type="button"
        class="pc__plus"
        aria-label="Add"
        data-testid="composer-plus"
        @click="plusOpen = true"
      >
        <Plus :size="18" />
      </button>
      <textarea
        ref="textarea"
        :value="draft.text"
        class="pc__input phone-composer-input"
        rows="1"
        :placeholder="actions.disabled.value ? 'This session is archived' : 'Type a message…'"
        :disabled="actions.disabled.value"
        aria-label="Message"
        data-testid="phone-composer-input"
        enterkeyhint="send"
        @input="onInput"
        @focus="focused = true"
        @blur="focused = false"
        @keydown.enter.exact.prevent="submit(false)"
      />
      <button
        v-if="offerSendNow"
        type="button"
        class="pc__now"
        aria-label="Send now, into the running turn"
        title="Send now, into the running turn"
        data-testid="composer-send-now"
        @click="submit(true)"
      >
        <CornerDownRight
          :size="14"
          aria-hidden="true"
        />Send now
      </button>
      <button
        type="submit"
        class="pc__send"
        :class="`pc__send--${primary}`"
        :disabled="actions.disabled.value || (primary === 'send' && !hasContent)"
        :aria-label="primary === 'stop' ? 'Stop' : primary === 'queue' ? 'Queue' : 'Send'"
        :data-testid="`composer-${primary}`"
      >
        <Square
          v-if="primary === 'stop'"
          :size="12"
          fill="currentColor"
        />
        <span
          v-else-if="primary === 'queue'"
          class="pc__queue-label"
        >Queue</span>
        <ArrowUp
          v-else
          :size="18"
        />
      </button>
    </form>

    <input
      ref="photoInput"
      type="file"
      accept="image/*"
      multiple
      hidden
      @change="onPhotos"
    >
    <input
      ref="cameraInput"
      type="file"
      accept="image/*"
      capture="environment"
      hidden
      @change="onPhotos"
    >

    <PhonePlusSheet
      :open="plusOpen"
      :machine-name="machineName"
      :supports-shell="actions.caps.value.supportsShell"
      :supports-side="actions.caps.value.supportsSide"
      @pick="onPlus"
      @close="plusOpen = false"
    />
    <PhoneFilePickSheet
      :open="filesOpen"
      :session-id="sessionId"
      @pick="(path) => insert(`@${path} `)"
      @close="filesOpen = false"
    />
    <PhoneChipSheet
      :open="chip !== null"
      :title="chip === 'agent' ? 'Agent' : chip === 'model' ? 'Model' : 'Effort'"
      :options="chip === 'agent' ? agentOptions : chip === 'model' ? modelOptions : effortOptions"
      :selected="chip === 'agent' ? agentId : chip === 'model' ? modelKey : draft.effort || 'medium'"
      @pick="pick"
      @close="chip = null"
    />
  </div>
</template>

<style scoped>
.pc {
  display: grid;
  flex: none;
  grid-template-columns: minmax(0, 1fr);
  gap: 6px;
  padding: 6px 10px 8px;
  border-top: 1px solid var(--border);
  background: var(--main-bg);
}

.pc__queue {
  display: grid;
  gap: 4px;
}

.pc__queued {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 40px;
  padding: 4px 4px 4px 10px;
  border: 1px dashed var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  font-size: 13px;
}

.pc__next {
  font-size: 10px;
  font-weight: 700;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--accent);
}

.pc__queued-text {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.pc__mini {
  display: inline-flex;
  align-items: center;
  min-height: 32px;
  padding: 0 8px;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  font: inherit;
  font-size: 12px;
}

.pc__chips {
  display: flex;
  gap: 6px;
  overflow-x: auto;
}

.pc__chip {
  min-height: 32px;
  padding: 0 12px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--card-bg);
  color: var(--text);
  font: inherit;
  font-size: 12px;
  white-space: nowrap;
}

.pc__photos {
  display: flex;
  gap: 6px;
}

.pc__photo {
  position: relative;
}

.pc__photo img {
  width: 52px;
  height: 52px;
  border-radius: var(--radius-btn);
  object-fit: cover;
}

.pc__photo-x {
  position: absolute;
  top: -4px;
  right: -4px;
  display: grid;
  width: 20px;
  height: 20px;
  place-items: center;
  border: 0;
  border-radius: 50%;
  background: var(--text);
  color: var(--main-bg);
}

.pc__error {
  font-size: 12px;
  color: var(--error);
}

.pc__box {
  display: flex;
  min-width: 0;
  align-items: flex-end;
  gap: 6px;
  padding: 4px;
  border: 1px solid var(--border);
  border-radius: 22px;
  background: var(--card-bg);
}

.pc__plus,
.pc__send {
  display: grid;
  width: 36px;
  height: 36px;
  flex: none;
  place-items: center;
  border: 0;
  border-radius: 50%;
}

.pc__plus {
  background: transparent;
  color: var(--muted);
}

.pc__input {
  flex: 1;
  min-width: 0;
  min-height: 36px;
  max-height: 160px;
  padding: 7px 4px;
  border: 0;
  background: transparent;
  color: var(--text);
  font: inherit;
  font-size: 16px;
  line-height: 1.4;
  resize: none;
  outline: none;
}

.pc__box:focus-within {
  border-color: color-mix(in srgb, var(--accent) 50%, var(--border));
}

.pc__send {
  background: var(--accent);
  color: #fff;
}

.pc__send:disabled {
  opacity: 0.45;
}

.pc__send--stop {
  background: var(--text);
  color: var(--main-bg);
}

.pc__send--queue {
  width: auto;
  padding: 0 12px;
  border-radius: 18px;
}

.pc__queue-label {
  font-size: 13px;
  font-weight: 600;
}

.pc__now {
  display: inline-flex;
  flex: none;
  align-items: center;
  gap: 4px;
  height: 36px;
  padding: 0 8px;
  border: 1px solid var(--border);
  border-radius: 18px;
  background: var(--main-bg);
  color: var(--text);
  font: inherit;
  font-size: 13px;
  white-space: nowrap;
}
</style>
