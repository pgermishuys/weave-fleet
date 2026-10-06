<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { ArrowUp, X } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import PhoneMarkdown from "@/components/phone/session/PhoneMarkdown.vue";
import { useSessionStream } from "@/composables/use-session-stream";
import { useSideConversation } from "@/composables/use-side-conversation";
import PhoneGlyph from "@/components/phone/PhoneGlyph.vue";
import { foldMessages } from "@/lib/phone/fold-steps";
import { autogrow } from "@/lib/phone/keyboard";

/**
 * A side question (`/btw`) as a full-height sheet over the session: its own conversation and composer, a fork that
 * doesn't touch the session's turn. Close discards it; Keep makes it a session of its own.
 */
const props = defineProps<{ open: boolean; sessionId: string }>();
const emit = defineEmits<{ (event: "close"): void; (event: "kept", sessionId: string): void }>();

const side = useSideConversation(() => props.sessionId);
const sideId = computed(() => side.side.value?.sessionId ?? "");
const stream = useSessionStream(sideId, () => Boolean(sideId.value));
const blocks = computed(() => foldMessages(stream.messages.value).filter((block) => block.kind === "user" || block.kind === "text"));
const question = shallowRef("");

async function ask(): Promise<void> {
  const text = question.value.trim();
  if (!text) return;
  question.value = "";
  if (!(await side.ask(text))) question.value = text;
}

async function keep(): Promise<void> {
  const kept = await side.keep();
  if (kept) emit("kept", kept.sessionId);
}

async function discard(): Promise<void> {
  await side.close();
  emit("close");
}
</script>

<template>
  <BottomSheet
    :open="open"
    label="Side question"
    :detents="['large']"
    @close="emit('close')"
  >
    <template #head>
      <h2>Side question<span class="ph-sheet__sub">The session carries on; this stays here</span></h2>
      <button
        v-if="side.side.value"
        type="button"
        class="ph-btn ph-btn--outline ph-btn--sm"
        @click="keep"
      >
        Keep
      </button>
      <button
        type="button"
        class="ph-icon-btn"
        aria-label="Close"
        data-testid="sheet-close"
        @click="side.side.value ? discard() : emit('close')"
      >
        <X aria-hidden="true" />
      </button>
    </template>

    <div class="ph-convo scs__convo">
      <p
        v-if="!side.side.value && !side.starting.value"
        class="scs__empty"
      >
        Ask something without interrupting the agent. It sees the conversation so far, but its answer stays here, and
        the session carries on.
      </p>
      <template
        v-for="block in blocks"
        :key="block.key"
      >
        <p
          v-if="block.kind === 'user'"
          class="ph-umsg"
        >
          {{ block.text }}
        </p>
        <PhoneMarkdown
          v-else-if="block.kind === 'text'"
          :text="block.text"
        />
      </template>
      <p
        v-if="side.working.value || side.starting.value"
        class="ph-working"
      >
        <PhoneGlyph
          kind="working"
          label="Thinking"
        /><span class="ph-working__word">Thinking</span>
      </p>
      <p
        v-if="side.error.value"
        class="scs__error"
        role="alert"
      >
        {{ side.error.value }}
      </p>
    </div>

    <template #foot>
      <form
        class="ph-frame"
        @submit.prevent="ask"
      >
        <textarea
          v-model="question"
          class="phone-composer-input"
          rows="1"
          placeholder="Ask on the side…"
          aria-label="Side question"
          enterkeyhint="enter"
          data-testid="side-input"
          @input="autogrow($event.target as HTMLTextAreaElement)"
        />
        <div class="ph-frame__bar">
          <span class="ph-sel ph-sel--note">/btw · doesn't interrupt the agent</span>
          <button
            type="submit"
            class="ph-send"
            aria-label="Ask"
            :disabled="!question.trim()"
          >
            <ArrowUp aria-hidden="true" />
          </button>
        </div>
      </form>
    </template>
  </BottomSheet>
</template>

<style scoped>
.scs__convo {
  padding-top: 0;
}

.scs__empty {
  margin: 4px 2px 0;
  font-size: var(--ph-t-meta);
  line-height: 1.4;
  color: var(--muted);
}

.scs__error {
  margin: 0;
  font-size: var(--ph-t-meta);
  color: var(--error);
}
</style>
