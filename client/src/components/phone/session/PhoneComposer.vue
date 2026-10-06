<script setup lang="ts">
import { computed, nextTick, onMounted, shallowRef, useTemplateRef, watch } from "vue";
import { ArrowUp, ChevronDown, CornerDownRight, Plus, Square, X } from "lucide-vue-next";
import PhoneChipSheet, { type ChipOption } from "@/components/phone/session/PhoneChipSheet.vue";
import PhoneFilePickSheet from "@/components/phone/session/PhoneFilePickSheet.vue";
import PhonePlusSheet, { type PlusChoice } from "@/components/phone/session/PhonePlusSheet.vue";
import { useAgents } from "@/composables/use-agents";
import { primaryAction, useComposerActions } from "@/composables/use-composer-actions";
import { useDraftState } from "@/composables/use-draft-state";
import { useModels } from "@/composables/use-models";
import type { ImageAttachment } from "@/lib/client-types";
import { ALLOWED_IMAGE_MIMES, MAX_IMAGE_BYTES } from "@/lib/image-validation";
import { autogrow } from "@/lib/phone/keyboard";
import { flushHeld, heldFor, hold, removeHeld, type HeldMessage } from "@/lib/phone/outbox";

/**
 * The phone's composer, the desktop's ComposerFrame: a raised card with the text on top and a toolbar under it — +
 * (a photo, the camera, a file, a command, a side question), the agent and model (and effort, when the model has
 * it), each a sheet, and one round button: Send when idle, Queue while the agent works, Stop while it works and
 * nothing is typed, with Now beside it when the harness can steer. Return makes a new line (the arrow sends), as
 * messaging apps do. Queued messages wait above it marked "Next". Typing `@`, `!` or `/btw` still works. It sits in
 * the session's dock, which stays above the keyboard.
 */
const props = withDefaults(defineProps<{ sessionId: string; machineId: string; machineName: string; reachable?: boolean }>(), { reachable: true });
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
const plusOpen = shallowRef(false);
const filesOpen = shallowRef(false);
const chip = shallowRef<"agent" | "model" | "effort" | null>(null);
const photos = shallowRef<(ImageAttachment & { url: string })[]>([]);
const photoError = shallowRef<string | null>(null);
// Typed while the machine was away: held on the phone, sent in order when it answers again.
const held = shallowRef<HeldMessage[]>(heldFor(props.machineId, props.sessionId));
let flushing = false;

const hasContent = computed(() => draft.text.trim().length > 0 || photos.value.length > 0);
// While the machine is away the button holds what's typed: there's nothing to stop or steer.
const primary = computed(() => props.reachable ? primaryAction(actions.status.value, hasContent.value) : "send");
const offerSendNow = computed(() => props.reachable && actions.caps.value.canSteer && actions.status.value === "busy" && hasContent.value);

const agentId = computed(() => draft.agentId || defaultAgentId.value || "");
const modelKey = computed(() => draft.modelId || defaultModelKey.value || "");
const agentOptions = computed<ChipOption[]>(() => agents.value.map((agent) => ({ id: agent.id, label: agent.name, detail: agent.description })));
const modelOptions = computed<ChipOption[]>(() => models.value.map((model) => ({ id: model.selectionKey, label: model.name, detail: model.provider })));
const effortVariants = computed(() => modelsByKey.value[modelKey.value]?.variants ?? ["low", "medium", "high"]);
const effortOptions = computed<ChipOption[]>(() => effortVariants.value.map((variant) => ({ id: variant, label: variant.charAt(0).toUpperCase() + variant.slice(1) })));
const agentLabel = computed(() => agents.value.find((agent) => agent.id === agentId.value)?.name ?? "Agent");
const modelLabel = computed(() => models.value.find((model) => model.selectionKey === modelKey.value)?.name ?? "Model");
const effortLabel = computed(() => (draft.effort || "medium").replace(/^./, (c) => c.toUpperCase()));
/** Effort only where the model has levels of it. */
const hasEffort = computed(() => (modelsByKey.value[modelKey.value]?.variants?.length ?? 0) > 0);

/** Grows with what's typed, up to five lines, then scrolls. */
function resize(): void {
  autogrow(textareaRef.value);
}

function onInput(event: Event): void {
  setText((event.target as HTMLTextAreaElement).value);
  resize();
  // Queue and Send now appear as you type and narrow the field: measure again once they're in.
  void nextTick(resize);
}

function submit(steer = false): void {
  if (!props.reachable) {
    const text = draft.text.trim();
    if (!text) return;
    hold(props.machineId, props.sessionId, text);
    held.value = heldFor(props.machineId, props.sessionId);
    setText("");
    void nextTick(resize);
    return;
  }
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

/** Sends what's held, oldest first; what doesn't go stays, with the reason. */
async function flush(): Promise<void> {
  if (flushing || !held.value.length) return;
  flushing = true;
  try {
    held.value = await flushHeld(props.machineId, props.sessionId, (text) => actions.sendText(text));
  } finally {
    flushing = false;
  }
}

function editHeld(item: HeldMessage): void {
  removeHeld(props.machineId, props.sessionId, item.id);
  held.value = heldFor(props.machineId, props.sessionId);
  insert(item.text);
}

function pick(id: string): void {
  if (chip.value === "agent") setAgentId(id);
  else if (chip.value === "model") setModelId(id);
  else if (chip.value === "effort") setEffort(id);
}

onMounted(() => {
  void nextTick(resize);
  if (props.reachable) void flush();
});

watch(() => props.reachable, (reachable) => {
  if (reachable) void flush();
});

defineExpose({ insert, flush, focus: () => textareaRef.value?.focus() });
</script>

<template>
  <div
    class="pc"
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
          class="ph-sel"
          @click="actions.sendQueued(item)"
        >
          Send now
        </button>
        <button
          type="button"
          class="ph-icon-btn pc__x"
          :aria-label="`Remove ${item.text}`"
          @click="actions.removeQueued(item)"
        >
          <X aria-hidden="true" />
        </button>
      </li>
    </ol>

    <ol
      v-if="held.length"
      class="pc__queue"
      aria-label="Held"
    >
      <li
        v-for="item in held"
        :key="item.id"
        class="pc__queued"
        data-testid="phone-held"
      >
        <span class="pc__next pc__next--held">Held</span>
        <span class="pc__queued-text">{{ item.text }}</span>
        <button
          v-if="item.error && reachable"
          type="button"
          class="ph-sel"
          :title="item.error"
          @click="flush"
        >
          Retry
        </button>
        <button
          type="button"
          class="ph-sel"
          @click="editHeld(item)"
        >
          Edit
        </button>
      </li>
    </ol>

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
      class="ph-frame"
      @submit.prevent="submit(false)"
    >
      <textarea
        ref="textarea"
        :value="draft.text"
        class="phone-composer-input"
        rows="1"
        :placeholder="actions.disabled.value ? 'This session is archived' : `Message ${machineName}…`"
        :disabled="actions.disabled.value"
        aria-label="Message"
        data-testid="phone-composer-input"
        enterkeyhint="enter"
        @input="onInput"
        @keydown.enter.meta.prevent="submit(false)"
        @keydown.enter.ctrl.prevent="submit(false)"
      />
      <div class="ph-frame__bar">
        <button
          type="button"
          class="ph-icon-btn"
          aria-label="Add"
          data-testid="composer-plus"
          @click="plusOpen = true"
        >
          <Plus aria-hidden="true" />
        </button>
        <button
          v-if="agentOptions.length"
          type="button"
          class="ph-sel"
          data-testid="chip-agent"
          @mousedown.prevent
          @click="chip = 'agent'"
        >
          <span>{{ agentLabel }}</span><ChevronDown aria-hidden="true" />
        </button>
        <button
          v-if="modelOptions.length"
          type="button"
          class="ph-sel"
          data-testid="chip-model"
          @mousedown.prevent
          @click="chip = 'model'"
        >
          <span>{{ modelLabel }}</span><ChevronDown aria-hidden="true" />
        </button>
        <button
          v-if="hasEffort"
          type="button"
          class="ph-sel"
          data-testid="chip-effort"
          @mousedown.prevent
          @click="chip = 'effort'"
        >
          <span>{{ effortLabel }}</span><ChevronDown aria-hidden="true" />
        </button>
        <span class="pc__spacer" />
        <button
          v-if="offerSendNow"
          type="button"
          class="ph-sel pc__now"
          aria-label="Send now, into the running turn"
          title="Send now, into the running turn"
          data-testid="composer-send-now"
          @click="submit(true)"
        >
          <CornerDownRight aria-hidden="true" />Now
        </button>
        <button
          type="submit"
          class="ph-send"
          :class="{ 'ph-send--stop': primary === 'stop', 'ph-send--queue': primary === 'queue' }"
          :disabled="actions.disabled.value || (primary === 'send' && !hasContent)"
          :aria-label="primary === 'stop' ? 'Stop' : primary === 'queue' ? 'Queue' : 'Send'"
          :data-testid="`composer-${primary}`"
        >
          <Square
            v-if="primary === 'stop'"
            class="pc__stop"
            fill="currentColor"
            aria-hidden="true"
          />
          <span v-else-if="primary === 'queue'">Queue</span>
          <ArrowUp
            v-else
            aria-hidden="true"
          />
        </button>
      </div>
    </form>

    <p
      v-if="!reachable"
      class="pc__away"
      data-testid="composer-held-note"
    >
      Sends when {{ machineName }} answers again.
    </p>

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
  grid-template-columns: minmax(0, 1fr);
  gap: 8px;
}

.pc__queue {
  display: grid;
  gap: 6px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.pc__queued {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 44px;
  padding: 4px 4px 4px 12px;
  border: 1px solid var(--ph-frame-edge);
  border-radius: var(--ph-r-btn);
  background: var(--ph-card);
  box-shadow: var(--ph-lift);
  font-size: 14px;
}

.pc__next {
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  color: var(--accent);
}

.pc__next--held {
  color: var(--muted);
}

.pc__queued-text {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.pc__x {
  width: 32px;
  height: 32px;
}

.pc__x svg {
  width: 16px;
  height: 16px;
}

.pc__away {
  margin: 0;
  font-size: var(--ph-t-meta);
  text-align: center;
  color: var(--muted);
}

.pc__spacer {
  flex: 1;
}

.pc__now {
  color: var(--text);
}

.pc__stop {
  width: 13px;
  height: 13px;
}

.pc__photos {
  display: flex;
  gap: 6px;
}

.pc__photo {
  position: relative;
}

.pc__photo img {
  width: 56px;
  height: 56px;
  border-radius: var(--ph-r-btn);
  object-fit: cover;
}

.pc__photo-x {
  position: absolute;
  top: -4px;
  right: -4px;
  display: grid;
  width: 22px;
  height: 22px;
  place-items: center;
  border-radius: 50%;
  background: var(--text);
  color: var(--ph-panel);
}

.pc__error {
  margin: 0 8px;
  font-size: var(--ph-t-meta);
  color: var(--error);
}
</style>
