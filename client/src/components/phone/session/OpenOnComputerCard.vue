<script setup lang="ts">
import { nextTick, shallowRef, useTemplateRef, watch } from "vue";
import { ArrowUp, ChevronRight } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import { showToast } from "@/composables/phone/use-phone-toast";
import { takeKeyboard } from "@/lib/phone/keyboard";
import { shareLink } from "@/lib/phone/share";

/**
 * Run a command: what a phone can't do well (a terminal) offers the closest thing that works — a one-off `!`
 * command that runs on Return, its output in the conversation — and a link to open a terminal on the computer.
 * The send button stays plain until there's something to run.
 */
const props = defineProps<{
  open: boolean;
  title: string;
  machineName: string;
  /** The address of this session on the computer. */
  link: string;
  supportsShell: boolean;
}>();
const emit = defineEmits<{ (event: "run", command: string): void; (event: "close"): void }>();

const command = shallowRef("");
const field = useTemplateRef<HTMLTextAreaElement>("field");

watch(() => props.open, async (open) => {
  if (!open) return;
  // The menu tap that opened this is holding the keyboard: hand it over.
  await nextTick();
  setTimeout(() => takeKeyboard(field.value), 0);
});

function run(): void {
  const text = command.value.trim().replace(/^!\s*/, "");
  if (!text) return;
  emit("run", text);
  command.value = "";
}

async function share(): Promise<void> {
  const outcome = await shareLink(`Fleet on ${props.machineName}`, props.link);
  if (outcome === "copied") showToast("Link copied. Open it on the computer.");
  else if (outcome === "failed") showToast("Couldn't share the link.");
}
</script>

<template>
  <BottomSheet
    :open="open"
    :label="title"
    :title="title"
    @close="emit('close')"
  >
    <div
      v-if="supportsShell"
      class="ph-sheet__pad"
    >
      <form
        class="ph-composer ph-glass-field ph-term"
        @submit.prevent="run"
      >
        <span
          class="ph-term__bang"
          aria-hidden="true"
        >!</span>
        <textarea
          ref="field"
          v-model="command"
          class="phone-composer-input"
          rows="1"
          placeholder="git log --oneline -3"
          aria-label="Command"
          autocapitalize="off"
          autocomplete="off"
          autocorrect="off"
          spellcheck="false"
          enterkeyhint="go"
          data-testid="ooc-command"
          @keydown.enter.prevent="run"
        />
        <button
          type="submit"
          class="ph-send ph-send--quiet"
          aria-label="Run"
          :disabled="!command.trim()"
        >
          <ArrowUp
            :size="22"
            :stroke-width="2.6"
            aria-hidden="true"
          />
        </button>
      </form>
      <p class="ph-group-f ooc__hint">
        Runs in the session's folder, with no model turn. The output shows in the conversation.
      </p>
    </div>
    <p
      v-else
      class="ph-group-f ooc__hint"
    >
      This harness can't run one-off commands. Terminals open on the computer.
    </p>
    <div class="ph-group ooc__group">
      <button
        type="button"
        class="ph-row"
        data-testid="ooc-share"
        @click="share"
      >
        <span class="ph-row__main">
          <span class="ph-row__title">Open a terminal on {{ machineName }}</span>
          <span class="ph-row__sub">Sends a link to the computer</span>
        </span>
        <ChevronRight
          class="ph-row__chev"
          :size="16"
          :stroke-width="3"
          aria-hidden="true"
        />
      </button>
    </div>
  </BottomSheet>
</template>

<style scoped>
.ooc__hint {
  margin: 8px 4px 0;
}

.ooc__group {
  margin-top: 18px;
}
</style>
