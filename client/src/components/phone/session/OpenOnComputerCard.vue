<script setup lang="ts">
import { nextTick, shallowRef, useTemplateRef, watch } from "vue";
import { ArrowUp, ChevronRight, SquareTerminal } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import { showToast } from "@/composables/phone/use-phone-toast";
import { autogrow, takeKeyboard } from "@/lib/phone/keyboard";
import { shareLink } from "@/lib/phone/share";

/**
 * Run a command: what a phone can't do well (a terminal) offers the closest thing that works — a one-off `!`
 * command in the composer's frame that runs on Return, its output in the conversation — and a link to open a
 * terminal on the computer. A machine without the web app (a node) has no page to link to, so it says to open the
 * session from home's Fleet instead. The send button stays quiet until there's something to run.
 */
const props = defineProps<{
  open: boolean;
  title: string;
  machineName: string;
  /** The folder it runs in, for the line under the title. */
  folder?: string | null;
  /** The address of this session on the computer; null when its machine has no web app. */
  link: string | null;
  /** The Fleet the phone is paired with, whose web app opens sessions on every machine. */
  homeName: string;
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
  if (!props.link) return;
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
    :subtitle="folder ? `in ${folder} on ${machineName}` : `on ${machineName}`"
    @close="emit('close')"
  >
    <div
      v-if="supportsShell"
      class="ph-sheet__pad"
    >
      <form
        class="ph-frame ph-frame--term"
        @submit.prevent="run"
      >
        <div class="ph-frame__top">
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
            @input="autogrow($event.target as HTMLTextAreaElement)"
            @keydown.enter.prevent="run"
          />
        </div>
        <div class="ph-frame__bar">
          <span class="ph-sel ph-sel--note">Runs without a model turn</span>
          <button
            type="submit"
            class="ph-send"
            aria-label="Run"
            :disabled="!command.trim()"
          >
            <ArrowUp aria-hidden="true" />
          </button>
        </div>
      </form>
    </div>
    <p
      v-else
      class="ph-foot ooc__hint"
    >
      This harness can't run one-off commands. Terminals open on the computer.
    </p>
    <div
      v-if="link"
      class="ph-card ooc__card"
    >
      <button
        type="button"
        class="ph-set"
        data-testid="ooc-share"
        @click="share"
      >
        <SquareTerminal
          class="ph-set__ic"
          aria-hidden="true"
        />
        <span class="ph-set__main">
          <span class="ph-set__t">Open a terminal on {{ machineName }}</span>
          <span class="ph-set__s">Sends a link to the computer</span>
        </span>
        <ChevronRight
          class="ph-set__chev"
          aria-hidden="true"
        />
      </button>
    </div>
    <p
      v-else
      class="ph-foot ooc__elsewhere"
      data-testid="ooc-elsewhere"
    >
      To open a terminal, open this session in Fleet on {{ homeName }}.
    </p>
  </BottomSheet>
</template>

<style scoped>
.ooc__hint {
  margin-top: 0;
}

.ooc__card,
.ooc__elsewhere {
  margin-top: 14px;
}
</style>
