<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { LoaderCircle } from "lucide-vue-next";
import ModActionButton from "@/components/mods/review/ModActionButton.vue";
import ModCodeDialog from "@/components/mods/review/ModCodeDialog.vue";
import ModReviewDialog from "@/components/mods/review/ModReviewDialog.vue";
import ModDraftMark from "@/components/mods/ModDraftMark.vue";
import { checkProblems } from "@/lib/mods/check-summary";
import type { ModDraft } from "@/lib/mods/kept";
import { checkDraft, fetchDraftFiles } from "@/lib/mods/kept-api";
import { useMachinesStore } from "@/stores/machines";
import { useModsStore } from "@/stores/mods";

/**
 * A draft the agent wrote, in the conversation: where it runs, and the ways to keep it, read it or stop it. The static
 * check runs only when the user asks (Check again, or the review), because each run can start the mod host.
 */
const props = defineProps<{ draft: ModDraft; phone?: boolean }>();

const store = useModsStore();
const machines = useMachinesStore();

const reviewOpen = shallowRef(false);
const codeOpen = shallowRef(false);
const toggling = shallowRef(false);
const actionError = shallowRef<string | null>(null);
const checking = shallowRef(false);
/** This card's own check, which replaces the draft's problem until the draft changes. */
const checked = shallowRef<{ text: string; failed: boolean } | null>(null);

const isOff = computed(() => props.draft.off !== null);
const stillWriting = computed(() => props.draft.description === null && props.draft.version === null);
const request = computed(() => props.draft.keepRequest ?? null);
const problem = computed(() => {
  if (checked.value) return checked.value;
  const found = props.draft.problem;
  if (!found) return null;
  return { text: `Check: ${found.message}${found.line == null ? "" : ` (line ${found.line})`}`, failed: true };
});
const status = computed(() => {
  if (props.draft.off?.by === "strikes") return "Turned off after three failures";
  return isOff.value ? "Off in this session" : "On in this session only";
});

watch(() => props.draft, () => { checked.value = null; });

const connection = () => machines.sessionTarget(props.draft.sessionId).connection;

function messageOf(caught: unknown, fallback: string): string {
  return caught instanceof Error ? caught.message : fallback;
}

/** Immediate: no turn, no confirmation. */
async function toggle(): Promise<void> {
  if (toggling.value) return;
  toggling.value = true;
  actionError.value = null;
  try {
    await store.setDraftOn(props.draft.sessionId, props.draft.name, isOff.value);
  } catch (caught) {
    actionError.value = messageOf(caught, "Couldn't change the draft.");
  } finally {
    toggling.value = false;
  }
}

async function recheck(): Promise<void> {
  if (checking.value) return;
  checking.value = true;
  const snapshot = props.draft;
  try {
    const report = await checkDraft(snapshot.sessionId, snapshot.name, connection());
    if (props.draft !== snapshot) return;
    if (!report) {
      checked.value = { text: "Fleet couldn't check it: the mod runtime isn't running", failed: false };
      return;
    }
    const { error } = checkProblems(report);
    checked.value = error
      ? { text: `Check: ${error.message}${error.line == null ? "" : ` (line ${error.line})`}`, failed: true }
      : { text: "No problems found", failed: false };
  } catch (caught) {
    if (props.draft === snapshot) checked.value = { text: messageOf(caught, "Couldn't check the draft."), failed: false };
  } finally {
    checking.value = false;
  }
}

const loadFiles = () => fetchDraftFiles(props.draft.sessionId, props.draft.name, connection());
</script>

<template>
  <div
    class="mod-draft-card"
    :class="{ 'mod-draft-card--phone': phone }"
    data-testid="mod-draft-card"
  >
    <div
      v-if="request"
      class="mod-draft-card__request"
      data-testid="mod-draft-request"
    >
      <div class="min-w-0">
        <p class="mod-draft-card__ask">
          The agent asks you to keep this
        </p>
        <p
          v-if="request.note"
          class="mod-draft-card__note"
        >
          {{ request.note }}
        </p>
      </div>
      <ModActionButton
        :phone="phone"
        size="sm"
        variant="primary"
        data-testid="mod-draft-review"
        @click="reviewOpen = true"
      >
        Review…
      </ModActionButton>
    </div>

    <div class="mod-draft-card__head">
      <ModDraftMark />
      <span class="mod-draft-card__name">{{ draft.name }}</span>
      <span class="mod-draft-card__status">{{ status }}</span>
    </div>

    <p
      v-if="draft.off?.by === 'strikes' && draft.off.error"
      class="mod-draft-card__line text-error"
    >
      {{ draft.off.error }}
    </p>
    <p
      v-if="stillWriting"
      class="mod-draft-card__line mod-draft-card__line--muted"
    >
      Still being written
    </p>
    <p
      v-else-if="draft.description"
      class="mod-draft-card__line"
    >
      {{ draft.description }}
    </p>
    <p
      v-if="draft.kept !== null"
      class="mod-draft-card__line mod-draft-card__line--muted"
    >
      Replaces your kept v{{ draft.kept }} in this session
    </p>
    <p
      v-if="problem"
      class="mod-draft-card__line"
      :class="problem.failed ? 'text-error' : 'mod-draft-card__line--muted'"
      data-testid="mod-draft-check"
    >
      {{ problem.text }}
    </p>
    <p
      v-if="actionError"
      class="mod-draft-card__line text-error"
      role="alert"
      data-testid="mod-draft-action-error"
    >
      {{ actionError }}
    </p>

    <div class="mod-draft-card__actions">
      <ModActionButton
        :phone="phone"
        size="sm"
        variant="primary"
        :disabled="stillWriting"
        data-testid="mod-draft-keep"
        @click="reviewOpen = true"
      >
        Keep…
      </ModActionButton>
      <ModActionButton
        :phone="phone"
        size="sm"
        variant="outline"
        data-testid="mod-draft-code"
        @click="codeOpen = true"
      >
        Show code
      </ModActionButton>
      <ModActionButton
        :phone="phone"
        size="sm"
        variant="outline"
        :disabled="toggling"
        data-testid="mod-draft-toggle"
        @click="toggle"
      >
        <LoaderCircle
          v-if="toggling"
          class="h-3.5 w-3.5 animate-spin"
          aria-hidden="true"
        />
        {{ isOff ? "Turn on again" : "Turn off" }}
      </ModActionButton>
      <button
        type="button"
        class="mod-draft-card__recheck"
        :disabled="checking || stillWriting"
        data-testid="mod-draft-recheck"
        @click="recheck"
      >
        {{ checking ? "Checking…" : "Check again" }}
      </button>
    </div>

    <ModReviewDialog
      v-model:open="reviewOpen"
      :session-id="draft.sessionId"
      :draft="draft"
      :phone="phone"
    />
    <ModCodeDialog
      v-model:open="codeOpen"
      :title="`${draft.name} · draft`"
      :phone="phone"
      :load="loadFiles"
    />
  </div>
</template>

<style scoped>
.mod-draft-card {
  display: grid;
  gap: 6px;
  max-width: 100%;
  margin: 8px 0;
  padding: 10px 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--surface, transparent);
  font-size: 13px;
}

.mod-draft-card__request {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin: -2px -4px 4px;
  padding: 8px 10px;
  border-radius: var(--radius-btn);
  background: color-mix(in srgb, var(--accent) 12%, transparent);
}

.mod-draft-card__ask {
  margin: 0;
  font-weight: 600;
}

.mod-draft-card__note {
  margin: 2px 0 0;
  color: var(--muted);
  overflow-wrap: anywhere;
}

.mod-draft-card__head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}

.mod-draft-card__name {
  font-family: var(--font-mono-stack);
  font-weight: 600;
  overflow-wrap: anywhere;
}

.mod-draft-card__status {
  color: var(--muted);
  font-size: 12px;
}

.mod-draft-card__line {
  margin: 0;
  overflow-wrap: anywhere;
}

.mod-draft-card__line--muted {
  color: var(--muted);
}

.mod-draft-card__actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin-top: 2px;
}

.mod-draft-card__recheck {
  padding: 2px 4px;
  color: var(--muted);
  font-size: 12px;
  text-decoration: underline;
  text-underline-offset: 2px;
}

.mod-draft-card__recheck:hover:not(:disabled) {
  color: var(--text);
}

.mod-draft-card__recheck:disabled {
  opacity: 0.6;
}

.mod-draft-card__recheck:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

/* Phone: the buttons stay in one wrapping row, the card stays short. */
.mod-draft-card--phone .mod-draft-card__actions {
  gap: 6px;
}

.mod-draft-card--phone .mod-draft-card__recheck {
  min-height: 36px;
  padding: 0 6px;
}

.mod-draft-card--phone .mod-draft-card__request {
  flex-wrap: wrap;
}
</style>
