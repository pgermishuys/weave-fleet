<script setup lang="ts">
import { computed, ref } from "vue";
import { ChevronRight } from "lucide-vue-next";
import DiffView from "@/components/session/DiffView.vue";
import { useModels } from "@/composables/use-models";
import { useSessionStream } from "@/composables/use-session-stream";
import { useSidebarMobile } from "@/composables/use-sidebar-mobile";
import { dispatchCommandEvent } from "@/lib/command-events";
import { formatCost, formatDuration, formatTokens } from "@/lib/format-utils";
import { deriveTurns, modelDisplayName, turnTotals, type SessionTurn, type TurnFile } from "@/lib/turns";

/**
 * The Turns tab: the rounds of this session, newest first — your prompt and everything the agent did
 * until it stopped. A turn shows the edits it made, from its own tool calls, so it reads the same on
 * every harness and stays true even after a later turn wrote over the same lines.
 */
const props = defineProps<{
  sessionId: string;
}>();

const { messages, delegations, hasMore, isLoadingOlder, loadOlder } = useSessionStream(() => props.sessionId);
const { models } = useModels();
const { hideRightPanel } = useSidebarMobile();

const turns = computed(() => deriveTurns(messages.value, delegations.value));
const totals = computed(() => turnTotals(turns.value));

const changedOnly = ref(false);
const shown = computed(() => (changedOnly.value ? turns.value.filter((turn) => !turn.readOnly) : turns.value));

// The newest turn is open to begin with; a click overrides that for any turn.
const openOverrides = ref<Record<string, boolean>>({});
const openFileKey = ref<string | null>(null);

function isOpen(turn: SessionTurn): boolean {
  return openOverrides.value[turn.id] ?? turn.id === turns.value[0]?.id;
}

function toggle(turn: SessionTurn): void {
  openOverrides.value = { ...openOverrides.value, [turn.id]: !isOpen(turn) };
}

function fileKey(turn: SessionTurn, file: TurnFile): string {
  return `${turn.id}|${file.path}`;
}

function toggleFile(turn: SessionTurn, file: TurnFile): void {
  const key = fileKey(turn, file);
  openFileKey.value = openFileKey.value === key ? null : key;
}

function modelName(turn: SessionTurn): string {
  return modelDisplayName(turn.modelId, models.value);
}

function took(turn: SessionTurn): string | null {
  return turn.durationMs === null ? null : formatDuration(Math.max(1, Math.round(turn.durationMs / 1000)));
}

function fileSummary(turn: SessionTurn): string {
  if (turn.readOnly) return "read only";
  return turn.files.length === 1 ? "1 file" : `${turn.files.length} files`;
}

/** The tail of the row's sub-line: how long it took, and how much it touched. */
function facts(turn: SessionTurn): string {
  const duration = took(turn);
  return [duration, fileSummary(turn)].filter(Boolean).map((part) => `· ${part}`).join(" ");
}

function promptOf(turn: SessionTurn): string {
  if (turn.prompt) return turn.prompt;
  return turn.hasPrompt ? "(no text in this prompt)" : "Before the loaded history";
}

/** Scroll the conversation to where this round began. On a phone the sheet steps aside first. */
function showInChat(turn: SessionTurn): void {
  hideRightPanel();
  dispatchCommandEvent("weave:command-show-message", { sessionId: props.sessionId, messageId: turn.id });
}
</script>

<template>
  <div class="turns-canvas">
    <div class="turns-canvas__summary">
      <button
        type="button"
        class="turns-canvas__filter"
        :aria-pressed="changedOnly"
        @click="changedOnly = !changedOnly"
      >
        Only turns that changed files
      </button>
      <span class="turns-canvas__order">Newest first</span>
      <span class="turns-canvas__totals">
        <span>{{ totals.turns }} {{ totals.turns === 1 ? "turn" : "turns" }}</span>
        <span
          v-if="totals.additions > 0"
          class="turns-canvas__adds"
        >+{{ totals.additions.toLocaleString() }}</span>
        <span
          v-if="totals.deletions > 0"
          class="turns-canvas__dels"
        >−{{ totals.deletions.toLocaleString() }}</span>
      </span>
    </div>

    <div class="turns-canvas__list">
      <p
        v-if="turns.length === 0"
        class="turns-canvas__empty"
      >
        No turns yet. The first prompt of this session starts one.
      </p>

      <p
        v-else-if="shown.length === 0"
        class="turns-canvas__empty"
      >
        No turn in the loaded history changed a file.
      </p>

      <div
        v-for="turn in shown"
        :key="turn.id"
        class="turn"
        :data-open="isOpen(turn)"
      >
        <button
          type="button"
          class="turn__row"
          :aria-expanded="isOpen(turn)"
          :title="turn.promptFull || undefined"
          @click="toggle(turn)"
        >
          <ChevronRight
            :size="12"
            class="turn__caret"
            aria-hidden="true"
          />
          <span class="turn__index">{{ turn.number }}</span>
          <span class="turn__copy">
            <span class="turn__prompt">{{ promptOf(turn) }}</span>
            <span class="turn__sub">
              <span class="turn__model">
                <span
                  class="turn__swatch"
                  aria-hidden="true"
                />{{ modelName(turn) }}
              </span>
              <span
                v-if="turn.modelChanged"
                class="turn__changed"
              >model changed</span>
              <span class="turn__facts">{{ facts(turn) }}</span>
            </span>
          </span>
          <span class="turn__stats">
            <template v-if="turn.additions > 0 || turn.deletions > 0">
              <span
                v-if="turn.additions > 0"
                class="turns-canvas__adds"
              >+{{ turn.additions }}</span>
              <span
                v-if="turn.deletions > 0"
                class="turns-canvas__dels"
              >−{{ turn.deletions }}</span>
            </template>
            <span
              v-else
              class="turn__none"
            >—</span>
          </span>
        </button>

        <div
          v-if="isOpen(turn)"
          class="turn__detail"
        >
          <div class="turn__meta">
            <span>Model <b>{{ modelName(turn) }}</b></span>
            <span v-if="turn.agent">Agent <b>{{ turn.agent }}</b></span>
            <span v-if="took(turn)">Took <b>{{ took(turn) }}</b></span>
            <span v-if="turn.tokensInput > 0 || turn.tokensOutput > 0">
              Tokens <b>{{ formatTokens(turn.tokensInput) }} in · {{ formatTokens(turn.tokensOutput) }} out</b>
            </span>
            <span v-if="turn.cost > 0">Cost <b>{{ formatCost(turn.cost) }}</b></span>
          </div>

          <div v-if="turn.files.length > 0">
            <p class="turn__label">
              Wrote this turn
            </p>
            <template
              v-for="file in turn.files"
              :key="file.path"
            >
              <button
                type="button"
                class="file-row"
                :class="{ 'file-row--open': openFileKey === fileKey(turn, file) }"
                :title="file.path"
                :aria-expanded="openFileKey === fileKey(turn, file)"
                @click="toggleFile(turn, file)"
              >
                <span class="file-row__name">{{ file.name }}</span>
                <span class="file-row__dir">{{ file.dir }}</span>
                <span
                  v-if="file.created"
                  class="file-row__badge"
                >new</span>
                <span class="file-row__stats">
                  <span
                    v-if="file.additions > 0"
                    class="turns-canvas__adds"
                  >+{{ file.additions }}</span>
                  <span
                    v-if="file.deletions > 0"
                    class="turns-canvas__dels"
                  >−{{ file.deletions }}</span>
                </span>
              </button>
              <div
                v-if="openFileKey === fileKey(turn, file)"
                class="turn__diff"
              >
                <DiffView
                  v-if="file.diff.length > 0"
                  :lines="[...file.diff]"
                />
                <p
                  v-else
                  class="turn__diff-empty"
                >
                  This harness didn't send the lines for this edit.
                </p>
              </div>
            </template>
          </div>

          <p
            v-else
            class="turn__note"
          >
            Nothing was written this turn<template v-if="turn.toolCount > 0">
              — {{ turn.toolCount }} {{ turn.toolCount === 1 ? "tool call" : "tool calls" }}, all reads
            </template>.
          </p>

          <div v-if="turn.toolCount > 0">
            <p class="turn__label">
              Also ran
            </p>
            <div class="turn__chips">
              <span class="turn__chip">
                {{ turn.toolCount }} {{ turn.toolCount === 1 ? "tool" : "tools" }}
                <template v-if="turn.editCount > 0"> · <b>{{ turn.editCount }} {{ turn.editCount === 1 ? "edit" : "edits" }}</b></template>
              </span>
              <span
                v-for="(command, index) in turn.commands"
                :key="`${turn.id}-cmd-${index}`"
                class="turn__chip"
                :title="command.label"
              >
                <span class="turn__chip-label">{{ command.label }}</span>
                <template v-if="command.ok !== null">
                  <span aria-hidden="true">·</span>
                  <b>{{ command.ok ? "passed" : "failed" }}</b>
                </template>
              </span>
            </div>
          </div>

          <div v-if="turn.delegations.length > 0">
            <p class="turn__label">
              Subagents
            </p>
            <div class="turn__chips">
              <span
                v-for="subagent in turn.delegations"
                :key="subagent.id"
                class="turn__chip"
                :title="subagent.title"
              >
                <span class="turn__chip-label">{{ subagent.title }}</span>
                <span aria-hidden="true">·</span>
                <b>{{ subagent.status }}</b>
              </span>
            </div>
          </div>

          <button
            type="button"
            class="turn__jump"
            @click="showInChat(turn)"
          >
            Show this turn in the chat ↑
          </button>
        </div>
      </div>

      <div
        v-if="turns.length > 0 && hasMore"
        class="turns-canvas__older"
      >
        <span>Older turns are not loaded yet.</span>
        <button
          type="button"
          class="turns-canvas__older-button"
          :disabled="isLoadingOlder"
          @click="loadOlder()"
        >
          {{ isLoadingOlder ? "Loading…" : "Load older" }}
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.turns-canvas {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  /* The rows answer to the panel's width, not the window's: this also renders in the phone sheet. */
  container-type: inline-size;
}

.turns-canvas__summary {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 10px;
  padding: 9px 12px;
  border-bottom: 1px solid var(--border);
  font-size: 12px;
  color: var(--muted);
}

.turns-canvas__filter {
  height: 26px;
  padding: 0 10px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--card-bg);
  color: var(--muted);
  font: inherit;
  font-size: 12px;
  cursor: pointer;
  transition: background var(--transition), color var(--transition), border-color var(--transition);
}

.turns-canvas__filter:hover {
  color: var(--text);
}

.turns-canvas__filter[aria-pressed="true"] {
  border-color: color-mix(in srgb, var(--accent) 45%, transparent);
  background: var(--accent-dim);
  color: var(--text);
}

.turns-canvas__filter:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.turns-canvas__order {
  white-space: nowrap;
}

.turns-canvas__totals {
  margin-left: auto;
  display: inline-flex;
  gap: 8px;
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  font-variant-numeric: tabular-nums;
}

.turns-canvas__adds {
  color: var(--running);
}

.turns-canvas__dels {
  color: var(--error);
}

.turns-canvas__list {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  padding: 6px;
  scrollbar-width: thin;
}

.turns-canvas__empty {
  margin: 0;
  padding: 14px 12px;
  font-size: 13px;
  color: var(--muted);
}

.turn {
  border-radius: var(--radius-card);
  /* Turns off screen skip layout and paint; a session can run to hundreds of them. */
  content-visibility: auto;
  contain-intrinsic-size: auto 46px;
}

.turn + .turn {
  margin-top: 1px;
}

.turn[data-open="true"] {
  content-visibility: visible;
}

.turn__row {
  display: flex;
  align-items: center;
  gap: 9px;
  width: 100%;
  box-sizing: border-box;
  padding: 8px 10px;
  border: 0;
  border-radius: var(--radius-card);
  background: transparent;
  color: var(--text);
  font: inherit;
  text-align: left;
  cursor: pointer;
  transition: background var(--transition);
}

.turn__row:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.turn__row:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.turn[data-open="true"] > .turn__row {
  background: color-mix(in srgb, var(--text) 6%, transparent);
}

.turn__caret {
  flex-shrink: 0;
  color: var(--muted);
  transition: transform var(--transition);
}

.turn[data-open="true"] .turn__caret {
  transform: rotate(90deg);
}

.turn__index {
  flex-shrink: 0;
  min-width: 22px;
  font-family: var(--font-mono-stack);
  font-size: 11px;
  color: var(--muted);
  font-variant-numeric: tabular-nums;
}

.turn__copy {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 1px;
}

.turn__prompt {
  font-size: 13px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.turn__sub {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  font-size: 11.5px;
  color: var(--muted);
  overflow: hidden;
  white-space: nowrap;
}

.turn__model {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  flex-shrink: 0;
}

.turn__swatch {
  width: 5px;
  height: 5px;
  flex-shrink: 0;
  border-radius: 50%;
  background: color-mix(in srgb, var(--accent) 75%, transparent);
}

.turn__changed {
  flex-shrink: 0;
  padding: 1px 6px;
  border-radius: 999px;
  background: var(--accent-dim);
  color: color-mix(in srgb, var(--text) 78%, transparent);
  font-size: 10.5px;
}

.turn__facts {
  overflow: hidden;
  text-overflow: ellipsis;
}

.turn__stats {
  flex-shrink: 0;
  display: inline-flex;
  gap: 7px;
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  font-variant-numeric: tabular-nums;
}

.turn__none {
  color: var(--muted);
}

.turn__detail {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 2px 10px 12px 41px;
}

.turn__meta {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 18px;
  font-size: 12px;
  color: var(--muted);
}

.turn__meta b {
  font-weight: 500;
  color: color-mix(in srgb, var(--text) 84%, transparent);
  font-variant-numeric: tabular-nums;
}

.turn__label {
  margin: 0 0 4px;
  font-family: var(--font-mono-stack);
  font-size: 10px;
  letter-spacing: 0.1em;
  text-transform: uppercase;
  color: var(--muted);
}

.turn__note {
  margin: 0;
  font-size: 12.5px;
  color: var(--muted);
}

.turn__chips {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.turn__chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  max-width: 100%;
  padding: 3px 9px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--card-bg);
  font-size: 11.5px;
  color: var(--muted);
  white-space: nowrap;
}

/* A long command keeps the chip on one line and ends in an ellipsis. */
.turn__chip-label {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
}

.turn__chip b {
  font-weight: 500;
  color: color-mix(in srgb, var(--text) 84%, transparent);
}

.turn__jump {
  align-self: flex-start;
  padding: 4px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: color-mix(in srgb, var(--text) 82%, transparent);
  font: inherit;
  font-size: 12px;
  cursor: pointer;
  transition: background var(--transition), color var(--transition);
}

.turn__jump:hover {
  background: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

.turn__jump:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

/* File rows read like the ones in Changes. */
.file-row {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  box-sizing: border-box;
  min-height: 28px;
  padding: 0 8px;
  content-visibility: auto;
  contain-intrinsic-size: auto 28px;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: color-mix(in srgb, var(--text) 86%, transparent);
  font: inherit;
  font-size: 13px;
  text-align: left;
  cursor: pointer;
  transition: background var(--transition), color var(--transition);
}

.file-row:hover,
.file-row--open {
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.file-row:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.file-row__name {
  flex-shrink: 0;
  white-space: nowrap;
}

.file-row__dir {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12px;
  color: color-mix(in srgb, var(--muted) 80%, transparent);
}

.file-row__badge {
  flex-shrink: 0;
  padding: 0 5px;
  border-radius: 4px;
  background: color-mix(in srgb, var(--text) 7%, transparent);
  font-size: 10px;
  color: var(--muted);
}

.file-row__stats {
  flex-shrink: 0;
  display: inline-flex;
  gap: 6px;
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  font-variant-numeric: tabular-nums;
}

.turn__diff {
  margin: 4px 0 8px 8px;
  min-width: 0;
}

/* DiffView brings its own frame and scrolling; a long edit is capped so the turn stays readable. */
.turn__diff :deep(.diff-view) {
  max-height: 340px;
}

.turn__diff-empty {
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
}

.turn__diff-empty {
  margin: 0;
  padding: 8px 10px;
  font-size: 12px;
  color: var(--muted);
}

.turns-canvas__older {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  padding: 10px;
  font-size: 12px;
  color: var(--muted);
}

.turns-canvas__older-button {
  padding: 3px 9px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: color-mix(in srgb, var(--text) 82%, transparent);
  font: inherit;
  font-size: 12px;
  cursor: pointer;
}

.turns-canvas__older-button:disabled {
  cursor: default;
  opacity: 0.6;
}

/* Squeezed right down, the row keeps its prompt, its model and its counts; the rest goes. */
@container (max-width: 300px) {
  .turn__facts,
  .turn__changed {
    display: none;
  }

  .turn__detail {
    padding-left: 16px;
  }
}

@media (prefers-reduced-motion: reduce) {
  .turn__row,
  .turn__caret,
  .file-row,
  .turn__jump {
    transition: none;
  }
}
</style>
