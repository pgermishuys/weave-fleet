<script setup lang="ts">
import { computed, shallowRef, useTemplateRef } from "vue";
import { Check, ChevronRight, CircleHelp, ShieldAlert, TriangleAlert } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import CommandText from "@/components/phone/CommandText.vue";
import PermissionChoices from "@/components/phone/PermissionChoices.vue";
import QuestionChoices from "@/components/phone/QuestionChoices.vue";
import { showToast } from "@/composables/phone/use-phone-toast";
import { collapse } from "@/lib/phone/animate";
import { dontAskAgainWording, permissionHeading } from "@/lib/tools";
import { haptic } from "@/lib/phone/haptics";
import type { InboxItem } from "@/lib/phone/inbox";
import { short } from "@/lib/phone/time";
import type { AnswerOutcome, PermissionReply } from "@/lib/push/answer";

/**
 * A session that needs you, as the desktop's permission and question cards draw it: what it wants and where from,
 * the session (a tap opens it), the command or the question, and quick answers — Allow once and More… (every choice,
 * in a sheet) for a permission; one tap on a question's first two options, and More…. Answers are optimistic: the
 * button turns green at once and the card folds away; if the machine says no, it comes back with why.
 */
const props = defineProps<{ item: InboxItem; now: number }>();
const emit = defineEmits<{
  (event: "open", item: InboxItem): void;
  (event: "permission", item: InboxItem, reply: PermissionReply, message: string | undefined, done: (outcome: AnswerOutcome) => void): void;
  (event: "question", item: InboxItem, answers: string[][], done: (outcome: AnswerOutcome) => void): void;
}>();

const cardRef = useTemplateRef<HTMLElement>("card");
const sheetRef = useTemplateRef<InstanceType<typeof BottomSheet>>("sheet");
const sheet = shallowRef(false);
/** The quick answer just sent ("once", or the option's label), shown as done while the card folds. */
const sent = shallowRef<string | null>(null);
const result = shallowRef<{ ok: boolean; text: string } | null>(null);
let collapsed = false;

const permission = computed(() => (props.item.ask?.kind === "permission" ? props.item.ask.ask : null));
const question = computed(() => (props.item.ask?.kind === "question" ? props.item.ask : null));
const quickOptions = computed(() => (question.value && !question.value.question.multiple ? question.value.question.options.slice(0, 2) : []));
const failed = computed(() => props.item.status === "error");
const heading = computed(() => (failed.value ? "Stopped with an error" : permission.value ? permissionHeading(permission.value) : question.value ? "Question" : "Waiting on you"));
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
  if (reply === "always") showToast(`Won't ask again for ${dontAskAgainWording(permission.value ?? { kind: "other", tool: "this", always: [] }).code ?? "this"} in this session`);
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
    class="ph-pcard"
    :class="{ 'ph-pcard--stale': item.stale, 'ph-pcard--error': failed }"
    data-testid="inbox-ask"
  >
    <div class="ph-pcard__head">
      <TriangleAlert
        v-if="failed"
        class="ph-pcard__icon"
        aria-hidden="true"
      />
      <ShieldAlert
        v-else-if="permission || !question"
        class="ph-pcard__icon"
        aria-hidden="true"
      />
      <CircleHelp
        v-else
        class="ph-pcard__icon"
        aria-hidden="true"
      />
      <span class="ph-pcard__title">{{ heading }}</span>
      <span class="ph-pcard__meta">
        <span class="ph-from">{{ item.machineName }}</span>{{ short(item.updatedAt, now) }}
      </span>
    </div>
    <button
      type="button"
      class="ph-pcard__open"
      :aria-label="`Open ${item.title} on ${item.machineName}`"
      @click="emit('open', item)"
    >
      <span class="ph-pcard__session"><span>{{ item.title }}</span><ChevronRight aria-hidden="true" /></span>
    </button>
    <CommandText
      v-if="permission?.title"
      :command="permission.title"
      :prompt="permission.kind === 'shell'"
      one-line
    />
    <p
      v-else-if="question"
      class="ph-pcard__q"
    >
      {{ question.question.question }}
    </p>

    <p
      v-if="result"
      class="ph-pcard__note"
      :class="{ 'ph-pcard__note--bad': !result.ok }"
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
          aria-hidden="true"
        />
        <span>{{ sent === "once" ? "Allowed" : sent === "always" ? "Always allowed" : sent === "reject" ? "Denied" : "Allow once" }}</span>
      </button>
      <button
        v-if="!sent"
        type="button"
        class="ph-btn ph-btn--outline"
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
        class="ph-btn ph-btn--outline"
        :class="{ 'ph-btn--done': sent === option.label }"
        :disabled="busy"
        @click="answerQuestion([option.label])"
      >
        <Check
          v-if="sent === option.label"
          aria-hidden="true"
        />
        <span>{{ option.label }}</span>
      </button>
      <button
        type="button"
        class="ph-btn ph-btn--outline"
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
      :title="permission ? permissionHeading(permission) : 'Question'"
      :subtitle="`${item.machineName} · ${item.title}`"
      :detents="['medium', 'large']"
      initial="medium"
      @close="sheet = false"
    >
      <PermissionChoices
        v-if="permission"
        :ask="permission"
        :busy="busy"
        @answer="answerPermission"
        @expand="sheetRef?.expand()"
      />
      <QuestionChoices
        v-else-if="question"
        :question="question.question"
        :more="question.more"
        :busy="busy"
        @answer="answerQuestion"
        @expand="sheetRef?.expand()"
      />
      <button
        type="button"
        class="ph-link ask__open-session"
        @click="openFromSheet"
      >
        Open the session
      </button>
    </BottomSheet>
  </article>
</template>

<style scoped>
.ask__open-session {
  margin-top: 10px;
}
</style>
