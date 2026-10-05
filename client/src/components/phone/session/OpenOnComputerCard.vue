<script setup lang="ts">
import { shallowRef } from "vue";
import { ArrowUp } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";

/**
 * What a phone can't do well (a terminal, the editor, an app preview) says so and offers the closest thing that works:
 * a one-off `!` command, or a link to open the session on the computer.
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
const shared = shallowRef<string | null>(null);

function run(): void {
  const text = command.value.trim().replace(/^!\s*/, "");
  if (!text) return;
  emit("run", text);
  command.value = "";
}

async function share(): Promise<void> {
  try {
    if (navigator.share) {
      await navigator.share({ title: `Fleet on ${props.machineName}`, url: props.link });
      shared.value = "Sent.";
      return;
    }
    await navigator.clipboard.writeText(props.link);
    shared.value = "Link copied. Open it on the computer.";
  } catch {
    shared.value = props.link;
  }
}
</script>

<template>
  <BottomSheet
    :open="open"
    :label="title"
    @close="emit('close')"
  >
    <h2 class="ooc__title">
      {{ title }}
    </h2>
    <p class="ooc__text">
      Terminals need a keyboard and a wide screen, so they open on the computer. On the phone you can run a one-off
      command and see its output in the conversation.
    </p>
    <form
      v-if="supportsShell"
      class="ooc__box"
      @submit.prevent="run"
    >
      <span
        class="ooc__bang"
        aria-hidden="true"
      >!</span>
      <input
        v-model="command"
        class="ooc__input phone-composer-input"
        placeholder="git log --oneline -3"
        aria-label="Command"
        autocapitalize="off"
        autocomplete="off"
        spellcheck="false"
        data-testid="ooc-command"
      >
      <button
        type="submit"
        class="ooc__send"
        aria-label="Run"
        :disabled="!command.trim()"
      >
        <ArrowUp :size="16" />
      </button>
    </form>
    <p
      v-if="supportsShell"
      class="ooc__hint"
    >
      Runs in the session's folder, no model turn.
    </p>
    <button
      type="button"
      class="ooc__row"
      @click="share"
    >
      Open {{ machineName }} on my computer <span class="ooc__hint">sends a link</span>
    </button>
    <p
      v-if="shared"
      class="ooc__hint"
      role="status"
    >
      {{ shared }}
    </p>
  </BottomSheet>
</template>

<style scoped>
.ooc__title {
  font-size: 15px;
  font-weight: 600;
}

.ooc__text {
  margin: 6px 0 12px;
  font-size: 14px;
  line-height: 1.5;
  color: var(--muted);
}

.ooc__box {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 4px 4px 4px 12px;
  border: 1px solid var(--border);
  border-radius: 22px;
  background: var(--card-bg);
}

.ooc__bang {
  font-family: var(--font-mono-stack);
  color: var(--muted);
}

.ooc__input {
  flex: 1;
  min-width: 0;
  min-height: 36px;
  border: 0;
  background: transparent;
  color: var(--text);
  font-family: var(--font-mono-stack);
  font-size: 15px;
  outline: none;
}

.ooc__send {
  display: grid;
  width: 34px;
  height: 34px;
  place-items: center;
  border: 0;
  border-radius: 50%;
  background: var(--accent);
  color: #fff;
}

.ooc__send:disabled {
  opacity: 0.45;
}

.ooc__hint {
  margin: 6px 0;
  font-size: 12px;
  color: var(--muted);
  overflow-wrap: anywhere;
}

.ooc__row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 100%;
  min-height: 48px;
  margin-top: 8px;
  padding: 0 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  color: var(--text);
  font: inherit;
  font-size: 14px;
}
</style>
