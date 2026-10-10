<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { LoaderCircle } from "lucide-vue-next";
import ModActionButton from "@/components/mods/review/ModActionButton.vue";
import ModCodeDialog from "@/components/mods/review/ModCodeDialog.vue";
import ModDialogShell from "@/components/mods/review/ModDialogShell.vue";
import { checkProblems, summarizeCheck } from "@/lib/mods/check-summary";
import type { ModCheckReport, ModDraft } from "@/lib/mods/kept";
import { checkDraft, fetchDraftFiles } from "@/lib/mods/kept-api";
import { useMachinesStore } from "@/stores/machines";
import { useModsStore } from "@/stores/mods";
import { useSessionsStore } from "@/stores/sessions";

/**
 * "Keep {name}?": what the check read from the draft, in plain words, before it's kept for all the user's sessions.
 * The draft is only checked here, when the user asks to review it.
 */
const props = defineProps<{
  sessionId: string;
  draft: ModDraft;
  /** On the phone it's a sheet, with the phone's buttons. */
  phone?: boolean;
}>();

const open = defineModel<boolean>("open", { required: true });
const emit = defineEmits<{ kept: [] }>();

const store = useModsStore();
const machines = useMachinesStore();
const sessions = useSessionsStore();

const NOTE_LIMIT = 2000;

const report = shallowRef<ModCheckReport | null>(null);
const loading = shallowRef(false);
const checkFailure = shallowRef<string | null>(null);
const note = shallowRef("");
const busy = shallowRef<"keep" | "decline" | null>(null);
const refused = shallowRef<string | null>(null);
const codeOpen = shallowRef(false);
let loadId = 0;

const request = computed(() => props.draft.keepRequest ?? null);
const rows = computed(() => (report.value ? summarizeCheck(report.value) : []));
const problems = computed(() => checkProblems(report.value));
const nextVersion = computed(() => (props.draft.kept ?? 0) + 1);
const sessionTitle = computed(() => sessions.sessionById(props.sessionId)?.session.title ?? null);
const meta = computed(() => {
  const parts = [`v${nextVersion.value}`];
  if (report.value) parts.push(`${report.value.lines} lines`);
  if (sessionTitle.value) parts.push(`written in “${sessionTitle.value}”`);
  return parts.join(" · ");
});
const canKeep = computed(() => !loading.value && busy.value === null && problems.value.error === null);

function errorMessage(caught: unknown, fallback: string): string {
  return caught instanceof Error ? caught.message : fallback;
}

watch(open, async (isOpen) => {
  if (!isOpen) return;
  const mine = ++loadId;
  report.value = null;
  checkFailure.value = null;
  refused.value = null;
  busy.value = null;
  note.value = request.value?.note ?? "";
  loading.value = true;
  try {
    const checked = await checkDraft(props.sessionId, props.draft.name, machines.sessionTarget(props.sessionId).connection);
    if (mine === loadId) report.value = checked;
  } catch (caught) {
    if (mine === loadId) checkFailure.value = errorMessage(caught, "Fleet couldn't check this mod.");
  } finally {
    if (mine === loadId) loading.value = false;
  }
}, { immediate: true });

async function keep(): Promise<void> {
  if (!canKeep.value) return;
  busy.value = "keep";
  refused.value = null;
  try {
    await store.keep(props.sessionId, props.draft.name, note.value);
    emit("kept");
    open.value = false;
  } catch (caught) {
    refused.value = errorMessage(caught, "Couldn't keep this mod.");
  } finally {
    busy.value = null;
  }
}

/** With the agent's request: tells it the user didn't keep the draft. Without one: just closes. */
async function decline(): Promise<void> {
  if (busy.value !== null) return;
  if (!request.value) {
    open.value = false;
    return;
  }
  busy.value = "decline";
  refused.value = null;
  try {
    await store.dismissKeepRequest(props.sessionId, props.draft.name);
    open.value = false;
  } catch (caught) {
    refused.value = errorMessage(caught, "Couldn't tell the agent.");
  } finally {
    busy.value = null;
  }
}

const LABELS: Record<string, string> = {
  draws: "Draws", listens: "Reacts to", reads: "Reads", state: "Remembers", store: "Saves",
  pages: "Pages", also: "Also", changes: "Changes", network: "Network", files: "Files",
};
/** Several draw sites share the first row's label (`draws-1`…). */
const labelOf = (key: string) => LABELS[key.replace(/-\d+$/, "")] ?? key;

const loadFiles = () => fetchDraftFiles(props.sessionId, props.draft.name, machines.sessionTarget(props.sessionId).connection);
</script>

<template>
  <ModDialogShell
    v-model:open="open"
    :label="`Keep ${draft.name}?`"
    :description="meta"
    :phone="phone"
    content-class="sm:max-w-xl"
    testid="mod-review-dialog"
  >
    <template #title>
      Keep <span class="font-mono">{{ draft.name }}</span>?
    </template>

    <div data-testid="mod-review-body">
      <span
        class="sr-only"
        data-testid="mod-review-meta"
      >{{ meta }}</span>
      <div
        v-if="loading"
        class="flex items-center gap-2 text-sm text-muted"
        data-testid="mod-review-loading"
      >
        <LoaderCircle
          class="h-4 w-4 animate-spin"
          aria-hidden="true"
        />
        Checking the mod…
      </div>

      <template v-else>
        <p
          v-if="!report"
          class="text-sm text-muted"
          data-testid="mod-review-unchecked"
        >
          {{ checkFailure ?? "Fleet couldn't check this mod yet: the mod runtime isn't running. Keep will check it." }}
        </p>

        <template v-else>
          <p
            v-if="problems.error"
            class="mb-2 text-sm text-error"
            role="alert"
            data-testid="mod-review-error"
          >
            Fix this first: {{ problems.error.message }}<template v-if="problems.error.where"> (line {{ problems.error.where }})</template>
          </p>
          <p
            v-if="problems.warnings > 0"
            class="mb-2 text-sm text-muted"
            data-testid="mod-review-warnings"
          >
            {{ problems.warnings }} {{ problems.warnings === 1 ? "warning" : "warnings" }}:
            {{ report.warnings.map((warning) => warning.message).join("; ") }}
          </p>

          <dl class="mod-review__list">
            <div
              v-for="row in rows"
              :key="row.key"
              class="mod-review__row"
              :class="{ 'mod-review__row--muted': row.muted }"
              data-testid="mod-review-row"
            >
              <dt>{{ labelOf(row.key) }}</dt>
              <dd>
                {{ row.text }}
                <code v-if="row.code">{{ row.code }}</code>
              </dd>
            </div>
          </dl>
        </template>
      </template>

      <label class="mt-3 block space-y-1">
        <span class="text-sm text-muted">Why keep it? (optional)</span>
        <textarea
          v-model="note"
          :maxlength="NOTE_LIMIT"
          rows="2"
          class="mod-review__note"
          data-testid="mod-review-note"
        />
      </label>

      <p
        v-if="refused"
        class="mt-2 text-sm text-error"
        role="alert"
        data-testid="mod-review-refused"
      >
        {{ refused }}
      </p>
    </div>

    <template #foot>
      <div
        class="mod-review__actions"
        data-testid="mod-review-foot"
      >
        <ModActionButton
          :phone="phone"
          variant="primary"
          :disabled="!canKeep"
          data-testid="mod-review-keep"
          @click="keep"
        >
          <LoaderCircle
            v-if="busy === 'keep'"
            class="h-4 w-4 animate-spin"
            aria-hidden="true"
          />
          Keep for all my sessions
        </ModActionButton>
        <ModActionButton
          :phone="phone"
          variant="outline"
          data-testid="mod-review-code"
          @click="codeOpen = true"
        >
          Show code
        </ModActionButton>
        <ModActionButton
          :phone="phone"
          variant="ghost"
          :disabled="busy !== null"
          data-testid="mod-review-decline"
          @click="decline"
        >
          {{ request ? "Discard" : "Cancel" }}
        </ModActionButton>
      </div>
      <p
        v-if="request"
        class="mod-review__hint"
      >
        Discard tells the agent you didn't keep it. The draft stays on in this session.
      </p>
    </template>
  </ModDialogShell>
  <ModCodeDialog
    v-model:open="codeOpen"
    :title="`${draft.name} · draft`"
    :phone="phone"
    :load="loadFiles"
  />
</template>

<style scoped>
.mod-review__list {
  display: grid;
  margin: 0;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  overflow: hidden;
}

.mod-review__row {
  display: grid;
  grid-template-columns: 6.5rem minmax(0, 1fr);
  gap: 12px;
  padding: 7px 12px;
  font-size: 13px;
}

.mod-review__row + .mod-review__row {
  border-top: 1px solid var(--border);
}

.mod-review__row dt {
  color: var(--muted);
}

.mod-review__row dd {
  margin: 0;
  overflow-wrap: anywhere;
}

.mod-review__row--muted dd {
  color: var(--muted);
}

.mod-review__note {
  width: 100%;
  resize: vertical;
  padding: 6px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: transparent;
  font-size: 13px;
}

.mod-review__note:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.mod-review__actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.mod-review__hint {
  margin: 8px 0 0;
  color: var(--muted);
  font-size: 12px;
}

.mod-review__row code {
  display: block;
  font-size: 12px;
  overflow-wrap: anywhere;
}

@media (max-width: 480px) {
  .mod-review__row {
    grid-template-columns: 1fr;
    gap: 2px;
  }
}
</style>
