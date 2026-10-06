<script setup lang="ts">
import { shallowRef, useTemplateRef } from "vue";
import { Check, ShieldAlert } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import CommandText from "@/components/phone/CommandText.vue";
import PermissionChoices from "@/components/phone/PermissionChoices.vue";
import type { PermissionAsk } from "@/composables/use-session-permissions";
import { dontAskAgain, permissionTitle } from "@/lib/phone/asks";
import { haptic } from "@/lib/phone/haptics";
import type { PermissionReply } from "@/lib/push/answer";

/**
 * A permission ask docked above the composer, compact, as the desktop's card: what it wants, the command (wrapped only
 * between words, three lines at most), Allow once and More… (every choice, in a sheet). Allow once turns green at
 * once; Later folds it into "1 waiting · Review".
 */
const props = defineProps<{
  ask: PermissionAsk;
  answer: (ask: PermissionAsk, reply: PermissionReply, message?: string) => Promise<void>;
  machineName: string;
  sessionTitle: string;
}>();
const emit = defineEmits<{ (event: "later"): void; (event: "answered", text: string): void }>();

const sheetRef = useTemplateRef<InstanceType<typeof BottomSheet>>("sheet");
const more = shallowRef(false);
const sent = shallowRef<PermissionReply | null>(null);
const error = shallowRef<string | null>(null);

async function onAnswer(reply: PermissionReply, message?: string): Promise<void> {
  more.value = false;
  sent.value = reply;
  error.value = null;
  if (reply === "once") haptic("success");
  try {
    await props.answer(props.ask, reply, message);
    emit("answered", reply === "reject" ? "Denied. The agent was told." : reply === "always" ? `Won't ask again for ${dontAskAgain(props.ask).code ?? props.ask.tool} in this session` : "Allowed once");
  } catch (failure) {
    sent.value = null;
    error.value = failure instanceof Error ? failure.message : String(failure);
  }
}
</script>

<template>
  <div
    class="ph-pcard ph-docked"
    data-testid="docked-permission"
  >
    <div class="ph-pcard__head">
      <ShieldAlert
        class="ph-pcard__icon"
        aria-hidden="true"
      />
      <span class="ph-pcard__title">{{ permissionTitle(ask) }}</span>
      <button
        type="button"
        class="ph-later"
        data-testid="docked-later"
        @click="emit('later')"
      >
        Later
      </button>
    </div>
    <CommandText
      v-if="ask.title"
      :command="ask.title"
      :prompt="ask.kind === 'shell'"
      class="ph-clamp3"
    />
    <p
      v-if="error"
      class="ph-pcard__note ph-pcard__note--bad"
      role="alert"
    >
      {{ error }}
    </p>
    <div class="ph-btns">
      <button
        type="button"
        class="ph-btn ph-btn--primary"
        :class="{ 'ph-btn--done': sent }"
        :disabled="sent !== null"
        data-testid="docked-allow-once"
        @click="onAnswer('once')"
      >
        <Check
          v-if="sent"
          aria-hidden="true"
        />
        <span>{{ sent === "once" ? "Allowed" : sent === "always" ? "Always allowed" : sent === "reject" ? "Denied" : "Allow once" }}</span>
      </button>
      <button
        v-if="!sent"
        type="button"
        class="ph-btn ph-btn--outline"
        data-testid="docked-more"
        @click="more = true"
      >
        <span>More…</span>
      </button>
    </div>

    <BottomSheet
      ref="sheet"
      :open="more"
      :label="permissionTitle(ask)"
      :title="permissionTitle(ask)"
      :subtitle="`${machineName} · ${sessionTitle}`"
      :detents="['medium', 'large']"
      initial="medium"
      @close="more = false"
    >
      <PermissionChoices
        :ask="ask"
        :busy="sent !== null"
        @answer="onAnswer"
        @expand="sheetRef?.expand()"
      />
    </BottomSheet>
  </div>
</template>
