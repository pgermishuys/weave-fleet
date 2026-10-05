<script setup lang="ts">
import { computed, shallowRef, useTemplateRef } from "vue";
import { Check, Monitor } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import CommandText from "@/components/phone/CommandText.vue";
import PermissionChoices from "@/components/phone/PermissionChoices.vue";
import QuestionChoices from "@/components/phone/QuestionChoices.vue";
import { showToast } from "@/composables/phone/use-phone-toast";
import { collapse } from "@/lib/phone/animate";
import { permissionTitle } from "@/lib/phone/asks";
import { haptic } from "@/lib/phone/haptics";
import { askPreview, type InboxItem } from "@/lib/phone/inbox";
import { ago } from "@/lib/phone/time";
import type { AnswerOutcome, PermissionReply } from "@/lib/push/answer";

/**
 * A session that needs you, as a card: machine and age, title, what it wants, and quick answers — Allow once and
 * More… (every choice, in a sheet) for a permission; a question's first two options and More…. Answers are
 * optimistic: the button flips at once and the card folds away; if the machine says no, it comes back with why.
 * Tapping the card opens the session.
 */
const props = defineProps<{ item: InboxItem; now: number }>();
const emit = defineEmits<{
  (event: "open", item: InboxItem): void;
  (event: "permission", item: InboxItem, reply: PermissionReply, message: string | undefined, done: (outcome: AnswerOutcome) => void): void;
  (event: "question", item: InboxItem, answers: string[][], done: (outcome: AnswerOutcome) => void): void;
}>();

const cardRef = useTemplateRef<HTMLElement>("card");
const sheetRef = useTemplateRef<InstanceType<typeof BottomSheet>>("sheet");
const preview = computed(() => askPreview(props.item));
const sheet = shallowRef(false);
/** The quick answer just sent ("once", or the option's label), shown as done while the card folds. */
const sent = shallowRef<string | null>(null);
const result = shallowRef<{ ok: boolean; text: string } | null>(null);
let collapsed = false;

const permission = computed(() => (props.item.ask?.kind === "permission" ? props.item.ask.ask : null));
const question = computed(() => (props.item.ask?.kind === "question" ? props.item.ask : null));
const quickOptions = computed(() => (question.value && !question.value.question.multiple ? question.value.question.options.slice(0, 2) : []));
const busy = computed(() => sent.value !== null);

function fold(): void {
  setTimeout(() => {
    if (!sent.value || !cardRef.value) return;
    collapsed = true;
    void collapse(cardRef.value);
  }, 450);
}

function unfold(): void {
  const el = cardRef.value;
  if (!el || !collapsed) return;
  collapsed = false;
  for (const key of ["height", "opacity", "margin-top", "margin-bottom", "padding-top", "padding-bottom", "overflow", "transition"]) el.style.removeProperty(key);
}

function finish(outcome: AnswerOutcome): void {
  if (outcome.ok || outcome.gone) {
    if (outcome.gone) showToast("Already answered.");
    return;
  }
  sent.value = null;
  unfold();
  result.value = { ok: false, text: outcome.error ?? "That didn't go through." };
}

function answerPermission(reply: PermissionReply, message?: string): void {
  sheet.value = false;
  result.value = null;
  sent.value = reply;
  if (reply === "once") haptic("success");
  if (reply === "always") showToast(`Won't ask again for ${permission.value?.always[0] ?? permission.value?.tool ?? "this"} in this session`);
  if (reply === "reject") showToast("Denied. The agent was told.");
  emit("permission", props.item, reply, message, finish);
  fold();
}

function answerQuestion(labels: string[]): void {
  sheet.value = false;
  result.value = null;
  sent.value = labels.join(", ");
  emit("question", props.item, [labels], finish);
  fold();
}

function openFromSheet(): void {
  sheet.value = false;
  emit("open", props.item);
}
</script>

<template>
  <article
    ref="card"
    class="ph-ask"
    :class="{ 'ph-ask--stale': item.stale, 'ph-ask--error': item.status === 'error' }"
    data-testid="inbox-ask"
  >
    <button
      type="button"
      class="ph-ask__open"
      :aria-label="`Open ${item.title} on ${item.machineName}`"
      @click="emit('open', item)"
    >
      <span class="ph-ask__meta">
        <span class="ph-machine"><Monitor
          :size="14"
          aria-hidden="true"
        />{{ item.machineName }}</span>
        <span>· {{ ago(item.updatedAt, now) }}</span>
      </span>
      <span class="ph-ask__title">{{ item.title }}</span>
      <span class="ph-ask__what">
        <b v-if="preview.lead === 'Asked:'">Asked:</b>
        <template v-else>{{ preview.lead }}</template>
        <template v-if="preview.detail && !preview.code">{{ ` ${preview.detail}` }}</template>
      </span>
    </button>
    <CommandText
      v-if="preview.detail && preview.code"
      :command="preview.detail"
      one-line
    />

    <p
      v-if="result"
      class="ph-ask__result"
      :class="{ 'ph-ask__result--bad': !result.ok }"
      role="alert"
    >
      {{ result.text }}
    </p>
    <div
      v-if="permission && !item.stale"
      class="ph-btns"
    >
      <button
        type="button"
        class="ph-btn ph-btn--primary"
        :class="{ 'ph-btn--done': sent }"
        :disabled="busy"
        data-testid="inbox-allow-once"
        @click="answerPermission('once')"
      >
        <Check
          v-if="sent"
          :size="20"
          :stroke-width="2.6"
          aria-hidden="true"
        />
        <span>{{ sent === "once" ? "Allowed" : sent === "always" ? "Always allowed" : sent === "reject" ? "Denied" : "Allow once" }}</span>
      </button>
      <button
        v-if="!sent"
        type="button"
        class="ph-btn"
        data-testid="inbox-more"
        @click="sheet = true"
      >
        <span>More…</span>
      </button>
    </div>
    <div
      v-else-if="question && !item.stale"
      class="ph-btns"
      :class="quickOptions.length === 2 ? 'ph-btns--q' : quickOptions.length === 1 ? 'ph-btns--q-one' : 'ph-btns--q-none'"
    >
      <button
        v-for="option in quickOptions"
        :key="option.label"
        type="button"
        class="ph-btn"
        :class="{ 'ph-btn--done': sent === option.label }"
        :disabled="busy"
        @click="answerQuestion([option.label])"
      >
        <Check
          v-if="sent === option.label"
          :size="20"
          :stroke-width="2.6"
          aria-hidden="true"
        />
        <span>{{ option.label }}</span>
      </button>
      <button
        type="button"
        class="ph-btn"
        :disabled="busy"
        data-testid="inbox-more"
        @click="sheet = true"
      >
        <span>{{ quickOptions.length ? "More…" : "Answer…" }}</span>
      </button>
    </div>

    <BottomSheet
      ref="sheet"
      :open="sheet"
      :label="`Answer ${item.title}`"
      :title="permission ? permissionTitle(permission) : 'Question'"
      :detents="['medium', 'large']"
      initial="medium"
      @close="sheet = false"
    >
      <PermissionChoices
        v-if="permission"
        :ask="permission"
        :busy="busy"
        :machine-name="item.machineName"
        :session-title="item.title"
        @answer="answerPermission"
        @expand="sheetRef?.expand()"
      />
      <QuestionChoices
        v-else-if="question"
        :question="question.question"
        :more="question.more"
        :busy="busy"
        :machine-name="item.machineName"
        :session-title="item.title"
        @answer="answerQuestion"
        @expand="sheetRef?.expand()"
      />
      <button
        type="button"
        class="ph-link-btn ask__open-session"
        @click="openFromSheet"
      >
        Open the session
      </button>
    </BottomSheet>
  </article>
</template>

<style scoped>
.ph-ask__meta,
.ph-ask__title,
.ph-ask__what {
  display: flex;
}

.ph-ask__title,
.ph-ask__what {
  display: block;
}

.ask__open-session {
  margin-top: 12px;
}
</style>
