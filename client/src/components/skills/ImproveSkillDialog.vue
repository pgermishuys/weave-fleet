<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { AlertCircle, LoaderCircle, Sparkles } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Textarea } from "@/components/ui/textarea";
import ProfileConfigEditor from "@/components/settings/ProfileConfigEditor.vue";
import SkillDiff from "@/components/skills/SkillDiff.vue";
import {
  estimateTokens,
  formatTokens,
  getSkill,
  improveSkill,
  saveSkillVersion,
  switchSkillVersion,
  versionLabel,
  type BuiltInSkillDetail,
  type ImproveTurn,
  type SkillProposal,
} from "@/lib/skill-versions";
import { useBuiltInSkillsStore } from "@/stores/built-in-skills";
import { useNoticesStore } from "@/stores/notices";

/**
 * Improve a built-in skill from the session it was used in. The user says what should change and picks what the
 * model may read; one question off the record proposes a new version, shown as a diff. Nothing is saved until Keep.
 */
const props = defineProps<{
  name: string;
  sessionId: string;
  /** The turn the skill was used in, split into the parts the user can include. */
  turn: ImproveTurn;
}>();

const open = defineModel<boolean>("open", { required: true });

/** What the question adds around the parts: the rules and the tags. */
const QUESTION_OVERHEAD_TOKENS = 250;

const store = useBuiltInSkillsStore();
const notices = useNoticesStore();

const detail = shallowRef<BuiltInSkillDetail | null>(null);
const note = shallowRef("");
const includeExchange = shallowRef(true);
const includeToolCalls = shallowRef(false);
const wholeConversation = shallowRef(false);
const stage = shallowRef<"compose" | "asking" | "proposal">("compose");
const proposal = shallowRef<SkillProposal | null>(null);
const draft = shallowRef("");
const editing = shallowRef(false);
const isSaving = shallowRef(false);
const error = shallowRef<string | null>(null);

const current = computed(() => detail.value?.yourContent ?? detail.value?.fleetContent ?? "");
const skillTokens = computed(() => estimateTokens(current.value));
const exchangeTokens = computed(() => estimateTokens(props.turn.exchange));
const toolTokens = computed(() => estimateTokens(props.turn.toolCalls));
const totalTokens = computed(() =>
  QUESTION_OVERHEAD_TOKENS
  + skillTokens.value
  + estimateTokens(note.value)
  + (wholeConversation.value ? 0 : (includeExchange.value ? exchangeTokens.value : 0) + (includeToolCalls.value ? toolTokens.value : 0)));
const context = computed(() => [
  includeExchange.value ? props.turn.exchange : "",
  includeToolCalls.value ? props.turn.toolCalls : "",
].filter(Boolean).join("\n\n"));
const canAsk = computed(() => detail.value !== null && note.value.trim().length > 0);
const nextVersion = computed(() => (detail.value?.versions[0]?.number ?? 0) + 1);

watch(open, async (isOpen) => {
  if (!isOpen) return;
  stage.value = "compose";
  proposal.value = null;
  editing.value = false;
  error.value = null;
  detail.value = null;
  includeExchange.value = props.turn.exchange.length > 0;
  try {
    detail.value = await getSkill(props.name);
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : `Couldn't load ${props.name}.`;
  }
}, { immediate: true });

async function ask(): Promise<void> {
  if (!canAsk.value) return;
  stage.value = "asking";
  error.value = null;
  try {
    proposal.value = await improveSkill(props.name, {
      sessionId: props.sessionId,
      note: note.value.trim(),
      context: wholeConversation.value ? undefined : context.value || undefined,
      wholeConversation: wholeConversation.value,
    });
    draft.value = proposal.value.content;
    editing.value = false;
    stage.value = "proposal";
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : `Couldn't ask for a better ${props.name}.`;
    stage.value = "compose";
  }
}

async function keep(): Promise<void> {
  if (!proposal.value || isSaving.value) return;
  const base = proposal.value.baseVersion;
  isSaving.value = true;
  error.value = null;
  try {
    const saved = await saveSkillVersion(props.name, draft.value, note.value.trim(), props.sessionId);
    store.update(saved);
    open.value = false;
    announce(saved, base);
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : `Couldn't save your version of ${props.name}.`;
  } finally {
    isSaving.value = false;
  }
}

/** A quiet notice with Undo, which puts back the version sessions got before. */
function announce(saved: BuiltInSkillDetail, previous: number | null): void {
  const id = `skill-version-${saved.name}-${saved.version}`;
  notices.post({
    id,
    title: `${saved.name} is now your version ${saved.version}`,
    body: "Sessions you start from now on use it.",
    icon: Sparkles,
    countdown: true,
    actions: [
      {
        label: "Undo",
        run: async () => {
          try {
            store.update(await switchSkillVersion(saved.name, previous));
            notices.remove(id);
          } catch (caught) {
            notices.update(id, {
              title: "Couldn't undo it",
              body: caught instanceof Error ? caught.message : "Switch back in Settings → Skills → Built in.",
              actions: [],
            });
          }
        },
      },
    ],
  });
}
</script>

<template>
  <Dialog v-model:open="open">
    <DialogContent
      class="max-h-[88dvh] overflow-y-auto sm:max-w-2xl"
      data-testid="improve-skill-dialog"
    >
      <DialogHeader>
        <DialogTitle class="flex flex-wrap items-center gap-2">
          Improve <span class="font-mono">{{ name }}</span>
          <span
            v-if="detail"
            class="improve-pill"
            :class="{ 'improve-pill--yours': detail.version }"
          >{{ versionLabel(detail.version) }}</span>
        </DialogTitle>
        <DialogDescription v-if="stage !== 'proposal'">
          Say what it should do differently. The model proposes a change to the skill, and nothing is saved until you
          keep it.
        </DialogDescription>
        <DialogDescription v-else>
          Keep it to make it your version {{ nextVersion }}: sessions you start from now on use it in place of
          {{ detail?.version ? `your version ${detail.version}` : "Fleet's" }}.
        </DialogDescription>
      </DialogHeader>

      <form
        v-if="stage !== 'proposal'"
        class="space-y-4"
        @submit.prevent="ask"
      >
        <div class="space-y-2">
          <label
            for="improve-skill-note"
            class="text-sm font-medium"
          >What should it do differently?</label>
          <Textarea
            id="improve-skill-note"
            v-model="note"
            rows="3"
            maxlength="2000"
            placeholder="e.g. Don't report naming or style as findings. fleet-simplify covers those."
            :disabled="stage === 'asking'"
            data-testid="improve-skill-note"
            @keydown.enter.exact.meta.prevent="ask"
            @keydown.enter.exact.ctrl.prevent="ask"
          />
        </div>

        <fieldset
          class="improve-sees"
          :disabled="stage === 'asking'"
        >
          <legend class="text-sm font-medium">
            What the model reads
          </legend>
          <label>
            <input
              type="checkbox"
              checked
              disabled
            >
            <span>The skill, <code>SKILL.md</code></span>
            <span class="improve-sees__tokens">{{ formatTokens(skillTokens) }}</span>
          </label>
          <label>
            <input
              type="checkbox"
              checked
              disabled
            >
            <span>Your note</span>
            <span class="improve-sees__tokens">{{ formatTokens(estimateTokens(note)) }}</span>
          </label>
          <label :class="{ 'improve-sees--off': wholeConversation || !turn.exchange }">
            <input
              v-model="includeExchange"
              type="checkbox"
              :disabled="wholeConversation || !turn.exchange"
              data-testid="improve-skill-include-exchange"
            >
            <span>What you asked, and the reply</span>
            <span class="improve-sees__tokens">{{ formatTokens(exchangeTokens) }}</span>
          </label>
          <label :class="{ 'improve-sees--off': wholeConversation || !turn.toolCalls }">
            <input
              v-model="includeToolCalls"
              type="checkbox"
              :disabled="wholeConversation || !turn.toolCalls"
              data-testid="improve-skill-include-tools"
            >
            <span>The tool calls in that turn</span>
            <span class="improve-sees__tokens">{{ formatTokens(toolTokens) }}</span>
          </label>
          <label>
            <input
              v-model="wholeConversation"
              type="checkbox"
              data-testid="improve-skill-whole-conversation"
            >
            <span>The whole conversation, from a copy of this session</span>
            <span class="improve-sees__tokens">whole session</span>
          </label>
          <p class="improve-sees__total">
            <span>Asked once, on this session's model</span>
            <span>{{ wholeConversation ? `${formatTokens(totalTokens)} + the session` : formatTokens(totalTokens) }}</span>
          </p>
        </fieldset>

        <div
          v-if="error"
          class="flex items-start gap-3 border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive"
          role="alert"
          data-testid="improve-skill-error"
        >
          <AlertCircle class="mt-0.5 h-4 w-4 shrink-0" />
          <p>{{ error }}</p>
        </div>

        <DialogFooter>
          <Button
            type="button"
            variant="outline"
            @click="open = false"
          >
            Cancel
          </Button>
          <Button
            type="submit"
            :disabled="!canAsk || stage === 'asking'"
            data-testid="improve-skill-ask"
          >
            <LoaderCircle
              v-if="stage === 'asking'"
              class="h-4 w-4 animate-spin"
            />
            {{ stage === "asking" ? "Asking the model…" : "Propose a change" }}
          </Button>
        </DialogFooter>
      </form>

      <div
        v-else-if="proposal"
        class="space-y-4"
      >
        <p class="improve-why">
          “{{ note.trim() }}”
        </p>

        <ProfileConfigEditor
          v-if="editing"
          v-model="draft"
          :label="`${name} SKILL.md`"
          filename="SKILL.md"
          @save="keep"
        />
        <SkillDiff
          v-else
          :before="proposal.base"
          :after="draft"
          :label="`${name}/SKILL.md`"
        />

        <p
          v-if="proposal.tokens"
          class="text-xs text-muted"
        >
          The question used {{ proposal.tokens.total.toLocaleString() }} tokens<template v-if="proposal.tokens.fromCache">
            ({{ proposal.tokens.fromCache.toLocaleString() }} from the cache)
          </template>.
        </p>

        <div
          v-if="error"
          class="flex items-start gap-3 border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive"
          role="alert"
        >
          <AlertCircle class="mt-0.5 h-4 w-4 shrink-0" />
          <p>{{ error }}</p>
        </div>

        <DialogFooter class="flex-wrap gap-2">
          <Button
            type="button"
            variant="outline"
            :disabled="isSaving"
            @click="stage = 'compose'"
          >
            Change the note
          </Button>
          <Button
            type="button"
            variant="outline"
            :disabled="isSaving"
            data-testid="improve-skill-edit"
            @click="editing = !editing"
          >
            {{ editing ? "Show the diff" : "Edit by hand" }}
          </Button>
          <Button
            type="button"
            :disabled="isSaving || draft === proposal.base"
            data-testid="improve-skill-keep"
            @click="keep"
          >
            <LoaderCircle
              v-if="isSaving"
              class="h-4 w-4 animate-spin"
            />
            Keep it
          </Button>
        </DialogFooter>
      </div>
    </DialogContent>
  </Dialog>
</template>

<style scoped>
.improve-pill {
  padding: 0 7px;
  border: 1px solid var(--border);
  border-radius: 999px;
  color: var(--muted);
  font-size: 11px;
  font-weight: 600;
}

.improve-pill--yours {
  border-color: transparent;
  background: var(--accent-dim);
  color: var(--accent);
}

.improve-sees {
  display: grid;
  gap: 2px;
  min-width: 0;
  margin: 0;
  padding: 0;
  border: 0;
}

.improve-sees legend {
  margin-bottom: 6px;
}

.improve-sees label {
  display: grid;
  grid-template-columns: 16px minmax(0, 1fr) auto;
  align-items: center;
  gap: 10px;
  padding: 6px 8px;
  border-radius: 6px;
  font-size: 13px;
  cursor: pointer;
}

.improve-sees label:hover {
  background: color-mix(in srgb, var(--text) 4%, transparent);
}

.improve-sees input {
  margin: 0;
  accent-color: var(--accent);
}

.improve-sees code {
  font-family: var(--font-mono-stack);
  font-size: 12px;
}

.improve-sees--off {
  color: var(--muted);
}

.improve-sees__tokens {
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.improve-sees__total {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  margin-top: 4px;
  padding: 8px 8px 0;
  border-top: 1px solid var(--border);
  color: var(--muted);
  font-size: 12.5px;
}

.improve-why {
  padding-left: 10px;
  border-left: 2px solid var(--accent);
  color: var(--muted);
  font-size: 13px;
}
</style>
