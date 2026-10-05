<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { Check } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import PermissionChoices from "@/components/phone/PermissionChoices.vue";
import QuestionChoices from "@/components/phone/QuestionChoices.vue";
import { askPreview, type InboxItem } from "@/lib/phone/inbox";
import type { AnswerOutcome, PermissionReply } from "@/lib/push/answer";
import { ago } from "@/lib/phone/time";

/**
 * A session that needs you, as a card: machine and age, title, what it wants, and quick answers. A permission
 * gets Allow once and More… (every choice, in a sheet); a question its first two options and More…. Tapping the
 * card opens the session.
 */
const props = defineProps<{ item: InboxItem; now: number }>();
const emit = defineEmits<{
  (event: "open", item: InboxItem): void;
  (event: "permission", item: InboxItem, reply: PermissionReply, message: string | undefined, done: (outcome: AnswerOutcome) => void): void;
  (event: "question", item: InboxItem, answers: string[][], done: (outcome: AnswerOutcome) => void): void;
}>();

const preview = computed(() => askPreview(props.item));
const sheet = shallowRef(false);
const busy = shallowRef(false);
const result = shallowRef<{ ok: boolean; text: string } | null>(null);

const permission = computed(() => props.item.ask?.kind === "permission" ? props.item.ask.ask : null);
const question = computed(() => props.item.ask?.kind === "question" ? props.item.ask : null);
const quickOptions = computed(() => question.value?.question.options.slice(0, 2) ?? []);

function finish(outcome: AnswerOutcome, success: string): void {
  busy.value = false;
  sheet.value = false;
  result.value = outcome.ok
    ? { ok: true, text: success }
    : { ok: false, text: outcome.gone ? "Already answered." : outcome.error ?? "That didn't go through." };
}

function answerPermission(reply: PermissionReply, message?: string): void {
  busy.value = true;
  const success = reply === "reject" ? "Denied." : reply === "always" ? "Allowed for this session." : "Allowed once.";
  emit("permission", props.item, reply, message, (outcome) => finish(outcome, success));
}

function answerQuestion(labels: string[]): void {
  busy.value = true;
  emit("question", props.item, [labels], (outcome) => finish(outcome, `Answered: ${labels.join(", ")}`));
}
</script>

<template>
  <article
    class="ask"
    :class="{ 'ask--stale': item.stale, 'ask--error': item.status === 'error' }"
    data-testid="inbox-ask"
  >
    <button
      type="button"
      class="ask__open"
      :aria-label="`Open ${item.title} on ${item.machineName}`"
      @click="emit('open', item)"
    >
      <span class="ask__meta">
        <span class="ask__machine">{{ item.machineName }}</span>
        <span>· {{ ago(item.updatedAt, now) }}</span>
      </span>
      <span class="ask__title">{{ item.title }}</span>
      <span class="ask__what">
        <b v-if="preview.lead === 'Asked:'">Asked:</b>
        <template v-else>{{ preview.lead }}</template>
        <template v-if="preview.detail && !preview.code">{{ `\u00a0${preview.detail}` }}</template>
      </span>
      <span
        v-if="preview.detail && preview.code"
        class="ask__cmd"
      >{{ preview.detail }}</span>
    </button>

    <p
      v-if="result"
      class="ask__result"
      :class="{ 'ask__result--bad': !result.ok }"
      role="status"
    >
      <Check
        v-if="result.ok"
        :size="14"
        aria-hidden="true"
      />{{ result.text }}
    </p>
    <div
      v-else-if="permission && !item.stale"
      class="ask__btns"
    >
      <Button
        size="sm"
        class="h-11 flex-1"
        :disabled="busy"
        data-testid="inbox-allow-once"
        @click="answerPermission('once')"
      >
        Allow once
      </Button>
      <Button
        variant="outline"
        size="sm"
        class="h-11 flex-1"
        :disabled="busy"
        @click="sheet = true"
      >
        More…
      </Button>
    </div>
    <div
      v-else-if="question && !item.stale"
      class="ask__btns"
    >
      <Button
        v-for="option in quickOptions"
        :key="option.label"
        variant="outline"
        size="sm"
        class="h-11 flex-1 truncate"
        :disabled="busy"
        @click="answerQuestion([option.label])"
      >
        {{ option.label }}
      </Button>
      <Button
        variant="outline"
        size="sm"
        class="h-11"
        :disabled="busy"
        @click="sheet = true"
      >
        More…
      </Button>
    </div>

    <BottomSheet
      :open="sheet"
      :label="`Answer ${item.title}`"
      @close="sheet = false"
    >
      <div class="ask__sheet-meta">
        <span class="ask__machine">{{ item.machineName }}</span>
        <span class="truncate">{{ item.title }}</span>
      </div>
      <PermissionChoices
        v-if="permission"
        :ask="permission"
        :busy="busy"
        @answer="answerPermission"
      />
      <QuestionChoices
        v-else-if="question"
        :question="question.question"
        :more="question.more"
        :busy="busy"
        @answer="answerQuestion"
        @skip="sheet = false"
      />
      <Button
        variant="ghost"
        class="mt-2 h-11 w-full"
        @click="sheet = false; emit('open', item)"
      >
        Open the session
      </Button>
    </BottomSheet>
  </article>
</template>

<style scoped>
.ask {
  display: grid;
  gap: 8px;
  padding: 12px;
  border: 1px solid color-mix(in srgb, var(--idle) 45%, transparent);
  border-radius: var(--radius-panel);
  background: var(--card-bg);
}

.ask--error {
  border-color: color-mix(in srgb, var(--error) 45%, transparent);
}

.ask--stale {
  opacity: 0.6;
}

.ask__open {
  display: grid;
  gap: 4px;
  padding: 0;
  border: 0;
  background: transparent;
  color: inherit;
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.ask__meta,
.ask__sheet-meta {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  font-size: 12px;
  color: var(--muted);
}

.ask__sheet-meta {
  margin-bottom: 10px;
}

.ask__machine {
  padding: 0 6px;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: var(--main-bg);
  font-size: 11px;
  font-weight: 500;
  color: var(--text);
}

.ask__title {
  font-size: 15px;
  font-weight: 600;
  line-height: 1.3;
}

.ask__what {
  font-size: 13px;
  color: var(--muted);
}

.ask__what b {
  font-weight: 500;
  color: var(--text);
}

.ask__cmd {
  overflow: hidden;
  padding: 6px 8px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--main-bg);
  font-family: var(--font-mono-stack);
  font-size: 12px;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.ask__btns {
  display: flex;
  gap: 8px;
}

.ask__result {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  color: var(--running);
}

.ask__result--bad {
  color: var(--error);
}
</style>
