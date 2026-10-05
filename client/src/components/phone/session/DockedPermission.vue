<script setup lang="ts">
import { shallowRef } from "vue";
import PermissionChoices from "@/components/phone/PermissionChoices.vue";
import type { PermissionAsk } from "@/composables/use-session-permissions";
import type { PermissionReply } from "@/lib/push/answer";

/** A permission ask in the composer's place: the choices in 44px rows, and "Later: let me read first". */
const props = defineProps<{ ask: PermissionAsk; answer: (ask: PermissionAsk, reply: PermissionReply, message?: string) => Promise<void> }>();
const emit = defineEmits<{ (event: "later"): void; (event: "answered", text: string): void }>();

const busy = shallowRef(false);
const error = shallowRef<string | null>(null);

async function onAnswer(reply: PermissionReply, message?: string): Promise<void> {
  busy.value = true;
  error.value = null;
  try {
    await props.answer(props.ask, reply, message);
    emit("answered", reply === "reject" ? "Denied" : reply === "always" ? "Allowed for this session" : "Allowed once");
  } catch (failure) {
    error.value = failure instanceof Error ? failure.message : String(failure);
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <div
    class="dock-ask"
    data-testid="docked-permission"
  >
    <p class="dock-ask__head">
      Needs you
    </p>
    <PermissionChoices
      :ask="ask"
      :busy="busy"
      compact
      @answer="onAnswer"
    />
    <p
      v-if="error"
      class="dock-ask__error"
      role="alert"
    >
      {{ error }}
    </p>
    <button
      type="button"
      class="dock-ask__later"
      @click="emit('later')"
    >
      Later: let me read first
    </button>
  </div>
</template>

<style scoped>
.dock-ask {
  display: grid;
  flex: none;
  gap: 6px;
  max-height: 70dvh;
  overflow-y: auto;
  padding: 8px 10px calc(env(safe-area-inset-bottom) + 8px);
  border-top: 1px solid var(--border);
  background: var(--main-bg);
}

.dock-ask__head {
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--idle);
}

.dock-ask__error {
  font-size: 12px;
  color: var(--error);
}

.dock-ask__later {
  min-height: 40px;
  border: 0;
  background: transparent;
  color: var(--muted);
  font: inherit;
  font-size: 13px;
}
</style>
