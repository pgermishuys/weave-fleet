<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { ArrowUp, X } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import PhoneMarkdown from "@/components/phone/session/PhoneMarkdown.vue";
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
    :detents="['large']"
    @close="emit('close')"
  >
    <template #head>
      <button
        v-if="side.side.value"
        type="button"
        class="ph-navbtn ph-glass ph-navbtn--text"
        @click="keep"
      >
        Keep
      </button>
      <h2>Side question</h2>
      <span class="ph-navbar__spacer" />
      <button
        type="button"
        class="ph-navbtn ph-glass"
        aria-label="Close"
        @click="side.side.value ? discard() : emit('close')"
      >
        <X
          :size="22"
          :stroke-width="2.4"
          aria-hidden="true"
        />
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
          class="ph-bubble scs__you"
        >
          {{ block.text }}
        </p>
        <PhoneMarkdown
          v-else-if="block.kind === 'text'"
          :text="block.text"
          class="ph-agent"
        />
      </template>
      <p
        v-if="side.working.value || side.starting.value"
        class="ph-working"
      >
        <span
          class="ph-typing"
          aria-hidden="true"
        ><i /><i /><i /></span>Thinking…
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
        class="ph-composer ph-glass-field"
        @submit.prevent="ask"
      >
        <textarea
          v-model="question"
          class="phone-composer-input"
          rows="1"
          placeholder="Ask on the side"
          aria-label="Side question"
          enterkeyhint="enter"
          data-testid="side-input"
        />
        <button
          type="submit"
          class="ph-send"
          aria-label="Ask"
          :disabled="!question.trim()"
        >
          <ArrowUp
            :size="22"
            :stroke-width="2.6"
            aria-hidden="true"
          />
        </button>
      </form>
    </template>
  </BottomSheet>
</template>

<style scoped>
.scs__convo {
  padding-top: 0;
}

.scs__empty {
  margin: 4px 4px 0;
  font-size: var(--ph-t-sub);
  line-height: 1.4;
  color: var(--muted);
}

.scs__you {
  margin: 0;
}

.scs__error {
  margin: 0;
  font-size: var(--ph-t-sub);
  color: var(--error);
}
</style>
