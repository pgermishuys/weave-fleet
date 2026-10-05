<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { ArrowUp, LoaderCircle } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import PhoneMarkdown from "@/components/phone/session/PhoneMarkdown.vue";
import { Button } from "@/components/ui/button";
import { useSessionStream } from "@/composables/use-session-stream";
import { useSideConversation } from "@/composables/use-side-conversation";
import { foldMessages } from "@/lib/phone/fold-steps";

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
    full
    @close="emit('close')"
  >
    <div class="scs">
      <header class="scs__head">
        <h2 class="scs__title">
          Side question <span class="scs__hint">/btw · the session carries on</span>
        </h2>
        <Button
          v-if="side.side.value"
          variant="ghost"
          size="sm"
          @click="keep"
        >
          Keep
        </Button>
        <Button
          v-if="side.side.value"
          variant="ghost"
          size="sm"
          @click="discard"
        >
          Close
        </Button>
      </header>
      <div class="scs__body">
        <p
          v-if="!side.side.value && !side.starting.value"
          class="scs__empty"
        >
          Ask something without interrupting the agent. It sees the conversation so far, but its answer stays here.
        </p>
        <template
          v-for="block in blocks"
          :key="block.key"
        >
          <p
            v-if="block.kind === 'user'"
            class="scs__you"
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
          class="scs__empty"
        >
          <LoaderCircle
            class="inline animate-spin"
            :size="14"
          /> Thinking…
        </p>
        <p
          v-if="side.error.value"
          class="scs__error"
          role="alert"
        >
          {{ side.error.value }}
        </p>
      </div>
      <form
        class="scs__box"
        @submit.prevent="ask"
      >
        <textarea
          v-model="question"
          class="scs__input phone-composer-input"
          rows="1"
          placeholder="Ask on the side…"
          aria-label="Side question"
          data-testid="side-input"
          @keydown.enter.exact.prevent="ask"
        />
        <button
          type="submit"
          class="scs__send"
          aria-label="Ask"
          :disabled="!question.trim()"
        >
          <ArrowUp :size="18" />
        </button>
      </form>
    </div>
  </BottomSheet>
</template>

<style scoped>
.scs {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
}

.scs__head {
  display: flex;
  align-items: center;
  gap: 4px;
}

.scs__title {
  flex: 1;
  font-size: 15px;
  font-weight: 600;
}

.scs__hint {
  display: block;
  font-size: 12px;
  font-weight: 400;
  color: var(--muted);
}

.scs__body {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 10px;
  min-height: 0;
  overflow-y: auto;
  padding: 10px 0;
}

.scs__empty {
  font-size: 13px;
  color: var(--muted);
}

.scs__error {
  font-size: 13px;
  color: var(--error);
}

.scs__you {
  align-self: flex-end;
  max-width: 86%;
  padding: 8px 12px;
  border-radius: var(--radius-panel);
  background: var(--accent-dim);
  font-size: 15px;
  white-space: pre-wrap;
}

.scs__box {
  display: flex;
  align-items: flex-end;
  gap: 6px;
  padding: 4px 4px 4px 12px;
  border: 1px solid var(--border);
  border-radius: 22px;
  background: var(--card-bg);
}

.scs__input {
  flex: 1;
  min-width: 0;
  min-height: 36px;
  padding: 7px 0;
  border: 0;
  background: transparent;
  color: var(--text);
  font: inherit;
  font-size: 16px;
  resize: none;
  outline: none;
}

.scs__send {
  display: grid;
  width: 36px;
  height: 36px;
  flex: none;
  place-items: center;
  border: 0;
  border-radius: 50%;
  background: var(--accent);
  color: #fff;
}

.scs__send:disabled {
  opacity: 0.45;
}
</style>
