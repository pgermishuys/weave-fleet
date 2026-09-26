<script setup lang="ts">
import { computed, onBeforeUnmount, shallowRef, watch } from "vue";
import { storeToRefs } from "pinia";
import { Check, CheckCircle2, Copy, Download, ExternalLink, KeyRound, LoaderCircle, X } from "lucide-vue-next";
import TerminalView from "@/components/terminal/TerminalView.vue";
import type { HarnessInfo, HarnessInstallChoice } from "@/api/client";
import { refreshAllHarnesses } from "@/composables/use-harnesses";
import {
  harnessDisplay,
  harnessLocation,
  harnessState,
  harnessStatusClasses,
  harnessStatusIcon,
  harnessStatusLabel,
} from "@/lib/harness-display";
import { closeSetupTerminal, createSetupTerminal, SETUP_TERMINALS_PATH } from "@/lib/terminal-api";
import { useAppShellStore } from "@/stores/app-shell";
import { usePreferencesStore } from "@/stores/preferences";

/**
 * The harnesses Fleet can help set up, each with Install or Sign in. Either opens the setup terminal under
 * the row with the harness's command typed in; the user presses Enter. While it's open Fleet checks the
 * harnesses again every few seconds, so a finished install shows up without a restart. A harness with no installer
 * to type on this platform (OpenCode 2 on Windows) links to its download instead. One that can go in more than one
 * place (OpenCode 2: its own folder, or as the main `opencode`) lets the user pick before installing.
 */

const props = defineProps<{
  harnesses: readonly HarnessInfo[];
  defaultHarnessType: string;
}>();

type SetupAction = "install" | "sign-in";

interface ActiveTerminal {
  harnessType: string;
  action: SetupAction;
  command: string;
  /** Null until the server has started the shell. */
  terminalId: string | null;
}

/** How often the harnesses are checked while the setup terminal is open. */
const CHECK_EVERY_MS = 3000;

const { config } = storeToRefs(useAppShellStore());
const preferences = usePreferencesStore();

const active = shallowRef<ActiveTerminal | null>(null);
/** Why the terminal couldn't open, with the command to run elsewhere. */
const failure = shallowRef<{ harnessType: string; message: string; command: string } | null>(null);
const copied = shallowRef(false);
/** The install place picked per harness type; the recommended one until the user picks another. */
const picked = shallowRef<Readonly<Record<string, string>>>({});
let checkTimer: ReturnType<typeof setInterval> | undefined;

/** Harnesses Fleet has an installer or a download for, the default one first. */
const rows = computed(() =>
  props.harnesses
    .filter((harness) => Boolean(harness.setup?.installCommand || harness.setup?.downloadUrl))
    .sort((a, b) => Number(b.type === props.defaultHarnessType) - Number(a.type === props.defaultHarnessType)),
);

/** The places to pick from, while it needs installing and there's more than one. */
function choicesFor(harness: HarnessInfo): readonly HarnessInstallChoice[] {
  const choices = harness.setup?.installChoices ?? [];
  return choices.length > 1 && actionFor(harness) === "install" ? choices : [];
}

function pickedChoice(harness: HarnessInfo): HarnessInstallChoice | null {
  const choices = choicesFor(harness);
  return choices.find((choice) => choice.id === picked.value[harness.type])
    ?? choices.find((choice) => choice.recommended)
    ?? choices[0]
    ?? null;
}

function pick(harness: HarnessInfo, choice: HarnessInstallChoice): void {
  picked.value = { ...picked.value, [harness.type]: choice.id };
}

function commandFor(harness: HarnessInfo, action: SetupAction): string | null {
  if (action === "sign-in") return harness.setup?.signInCommand ?? null;
  return pickedChoice(harness)?.command ?? harness.setup?.installCommand ?? null;
}

function actionFor(harness: HarnessInfo): SetupAction | null {
  switch (harnessState(harness)) {
    case "not-installed":
    case "not-working":
    // The install script installs the latest version over the old one.
    case "update-needed":
      return "install";
    case "sign-in-required":
      return harness.setup?.signInCommand ? "sign-in" : null;
    case "ready":
      return null;
  }
}

function subtitle(harness: HarnessInfo): string {
  switch (harnessState(harness)) {
    case "ready":
      return harnessLocation(harness) ?? "Ready.";
    case "sign-in-required":
      return "Installed. Sign in once so sessions can use your account.";
    case "not-working":
      return harness.reason ?? `${harness.displayName} isn't working.`;
    case "update-needed":
      return harness.reason ?? `${harness.displayName} is too old for Fleet.`;
    case "not-installed":
      return harnessDisplay(harness.type).pitch ?? harness.reason ?? `${harness.displayName} isn't installed.`;
  }
}

/** Shown once the harness the terminal was opened for is found. */
const found = computed(() => {
  const current = active.value;
  if (!current) return null;
  const harness = props.harnesses.find((candidate) => candidate.type === current.harnessType);
  if (!harness) return null;
  const state = harnessState(harness);
  if (current.action === "sign-in") {
    return state === "ready" ? `${harness.displayName} is signed in and ready.` : null;
  }
  if (state !== "ready" && state !== "sign-in-required") return null;
  const where = harness.executablePath ? ` at ${harness.executablePath}` : "";
  return `Found ${harness.displayName}${harness.version ? ` ${harness.version}` : ""}${where}. You don't need to restart Fleet.`;
});

async function start(harness: HarnessInfo, action: SetupAction): Promise<void> {
  const command = commandFor(harness, action);
  if (!command) return;
  await closeTerminal();
  failure.value = null;

  // Setting up a harness here means wanting to use it: turn it on (Claude Code starts off).
  if (!harness.userEnabled) void preferences.set(`${harness.type}.enabled`, "true").then(refreshAllHarnesses);

  if (!config.value.terminalEnabled) {
    failure.value = { harnessType: harness.type, message: "Terminals are turned off in this Fleet.", command };
    return;
  }

  active.value = { harnessType: harness.type, action, command, terminalId: null };
  try {
    const terminal = await createSetupTerminal(100, 14);
    if (active.value?.harnessType !== harness.type) {
      void closeSetupTerminal(terminal.id).catch(() => {});
      return;
    }
    active.value = { ...active.value, terminalId: terminal.id };
    startChecking();
  } catch (error) {
    active.value = null;
    failure.value = {
      harnessType: harness.type,
      message: error instanceof Error ? error.message : "The terminal couldn't start.",
      command,
    };
  }
}

/** The download to open by hand, when the harness needs installing and there's no installer to type. */
function downloadFor(harness: HarnessInfo): string | null {
  if (actionFor(harness) !== "install" || harness.setup?.installCommand) return null;
  return harness.setup?.downloadUrl ?? null;
}

/** What to know before installing (where it goes, its sign-in); shown until it's ready. */
function notesFor(harness: HarnessInfo): readonly string[] {
  return harnessState(harness) === "ready" ? [] : harness.setup?.notes ?? [];
}

function actionLabel(harness: HarnessInfo): string {
  if (actionFor(harness) === "sign-in") return `Sign in to ${harness.displayName}`;
  return harnessState(harness) === "update-needed" ? `Update ${harness.displayName}` : `Install ${harness.displayName}`;
}

/** Starts whatever the harness needs next: installing it, or signing in. */
async function startNext(harness: HarnessInfo): Promise<void> {
  const action = actionFor(harness);
  if (action) await start(harness, action);
}

function startChecking(): void {
  stopChecking();
  checkTimer = setInterval(refreshAllHarnesses, CHECK_EVERY_MS);
}

function stopChecking(): void {
  if (checkTimer !== undefined) clearInterval(checkTimer);
  checkTimer = undefined;
}

/** Ends the setup terminal, if one is open. */
async function closeTerminal(): Promise<void> {
  stopChecking();
  const terminalId = active.value?.terminalId;
  active.value = null;
  if (terminalId) {
    refreshAllHarnesses();
    await closeSetupTerminal(terminalId).catch(() => {});
  }
}

async function copyCommand(command: string): Promise<void> {
  try {
    await navigator.clipboard?.writeText(command);
    copied.value = true;
    setTimeout(() => (copied.value = false), 1500);
  } catch {
    // The command is on screen to select by hand.
  }
}

/** A finished sign-in has nothing left to do in the terminal. */
watch(found, (message) => {
  if (message && active.value?.action === "sign-in") stopChecking();
});

onBeforeUnmount(() => void closeTerminal());

defineExpose({ closeTerminal });
</script>

<template>
  <div
    class="harness-setup-rows"
    data-testid="harness-setup-rows"
  >
    <div
      v-for="harness in rows"
      :key="harness.type"
      class="harness-setup-rows__row"
      :data-testid="`harness-setup-row-${harness.type}`"
    >
      <div class="harness-setup-rows__head">
        <div class="harness-setup-rows__icon">
          <component
            :is="harnessDisplay(harness.type).icon"
            :size="17"
            aria-hidden="true"
          />
        </div>
        <div class="harness-setup-rows__name">
          <p>
            <b>{{ harness.displayName }}</b>
            <span
              v-if="harness.type === props.defaultHarnessType"
              class="harness-setup-rows__default"
            >Default</span>
          </p>
          <p
            class="harness-setup-rows__sub"
            :class="{ 'harness-setup-rows__sub--mono': harnessState(harness) === 'ready' }"
          >
            {{ subtitle(harness) }}
          </p>
        </div>
        <div class="harness-setup-rows__actions">
          <span
            class="inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[11px] font-medium"
            :class="harnessStatusClasses(harnessState(harness))"
          >
            <component
              :is="harnessStatusIcon(harnessState(harness))"
              :size="11"
              aria-hidden="true"
            />
            {{ harnessStatusLabel(harnessState(harness)) }}
          </span>
          <a
            v-if="downloadFor(harness)"
            :href="downloadFor(harness) ?? undefined"
            target="_blank"
            rel="noopener noreferrer"
            class="harness-setup-rows__btn"
            :class="{ 'harness-setup-rows__btn--primary': harness.type === props.defaultHarnessType }"
            :data-testid="`harness-setup-download-${harness.type}`"
          >
            <ExternalLink
              :size="14"
              aria-hidden="true"
            />
            Download {{ harness.displayName }}
          </a>
          <button
            v-else-if="actionFor(harness)"
            type="button"
            class="harness-setup-rows__btn"
            :class="{ 'harness-setup-rows__btn--primary': harness.type === props.defaultHarnessType || actionFor(harness) === 'sign-in' }"
            :disabled="active?.harnessType === harness.type && !found"
            :data-testid="`harness-setup-${actionFor(harness)}-${harness.type}`"
            @click="void startNext(harness)"
          >
            <KeyRound
              v-if="actionFor(harness) === 'sign-in'"
              :size="14"
              aria-hidden="true"
            />
            <Download
              v-else
              :size="14"
              aria-hidden="true"
            />
            {{ actionLabel(harness) }}
          </button>
        </div>
      </div>

      <div
        v-if="choicesFor(harness).length > 0"
        class="harness-setup-rows__choices"
        role="radiogroup"
        :aria-label="`Where to install ${harness.displayName}`"
        :data-testid="`harness-setup-choices-${harness.type}`"
      >
        <p class="harness-setup-rows__choices-title">
          Where to install it
        </p>
        <label
          v-for="choice in choicesFor(harness)"
          :key="choice.id"
          class="harness-setup-rows__choice"
          :class="{ 'harness-setup-rows__choice--picked': pickedChoice(harness)?.id === choice.id }"
          :data-testid="`harness-setup-choice-${harness.type}-${choice.id}`"
        >
          <input
            type="radio"
            :name="`harness-setup-choice-${harness.type}`"
            :value="choice.id"
            :checked="pickedChoice(harness)?.id === choice.id"
            :disabled="active?.harnessType === harness.type"
            @change="pick(harness, choice)"
          >
          <span class="harness-setup-rows__choice-text">
            <span class="harness-setup-rows__choice-label">
              {{ choice.label }}
              <span
                v-if="choice.recommended"
                class="harness-setup-rows__recommended"
              >Recommended</span>
            </span>
            <span class="harness-setup-rows__choice-description">{{ choice.description }}</span>
          </span>
        </label>
      </div>

      <ul
        v-if="notesFor(harness).length > 0"
        class="harness-setup-rows__notes"
        :data-testid="`harness-setup-notes-${harness.type}`"
      >
        <li
          v-for="note in notesFor(harness)"
          :key="note"
        >
          {{ note }}
        </li>
      </ul>

      <div
        v-if="active?.harnessType === harness.type"
        class="harness-setup-rows__terminal-wrap"
      >
        <div
          class="harness-setup-rows__terminal"
          data-testid="harness-setup-terminal"
        >
          <div class="harness-setup-rows__terminal-bar">
            <span>{{ active.action === "sign-in" ? `Sign in to ${harness.displayName}` : `Install ${harness.displayName}` }} · home folder</span>
            <button
              type="button"
              aria-label="Close terminal"
              @click="void closeTerminal()"
            >
              <X
                :size="13"
                aria-hidden="true"
              />
            </button>
          </div>
          <div class="harness-setup-rows__terminal-body">
            <TerminalView
              v-if="active.terminalId"
              session-id="setup"
              :terminal-id="active.terminalId"
              :base-path="SETUP_TERMINALS_PATH"
              :initial-input="active.command"
              :shown="true"
              @ended="void closeTerminal()"
            />
            <p
              v-else
              class="harness-setup-rows__starting"
            >
              <LoaderCircle
                :size="14"
                class="animate-spin"
                aria-hidden="true"
              />
              Starting a terminal…
            </p>
          </div>
          <p class="harness-setup-rows__hint">
            Check the command, then press Enter to run it. Fleet won't run it for you.
          </p>
        </div>
        <p
          v-if="found"
          class="harness-setup-rows__found"
          role="status"
          data-testid="harness-setup-found"
        >
          <CheckCircle2
            :size="14"
            aria-hidden="true"
          />
          {{ found }}
        </p>
      </div>

      <div
        v-if="failure?.harnessType === harness.type"
        class="harness-setup-rows__failure"
        role="alert"
      >
        <p>{{ failure.message }} Run this in your own terminal instead, then check again:</p>
        <div class="harness-setup-rows__copy">
          <code>{{ failure.command }}</code>
          <button
            type="button"
            class="harness-setup-rows__btn"
            @click="void copyCommand(failure.command)"
          >
            <Check
              v-if="copied"
              :size="14"
              aria-hidden="true"
            />
            <Copy
              v-else
              :size="14"
              aria-hidden="true"
            />
            {{ copied ? "Copied" : "Copy" }}
          </button>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.harness-setup-rows {
  display: grid;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
}

.harness-setup-rows__row {
  display: grid;
  gap: 12px;
  padding: 14px 16px;
}

.harness-setup-rows__row + .harness-setup-rows__row {
  border-top: 1px solid var(--border);
}

.harness-setup-rows__head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 12px;
}

.harness-setup-rows__icon {
  display: grid;
  flex: none;
  place-items: center;
  width: 34px;
  height: 34px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--main-bg);
  color: var(--text);
}

.harness-setup-rows__name {
  flex: 1 1 220px;
  min-width: 0;
}

.harness-setup-rows__name b {
  color: var(--text);
  font-size: 14px;
  font-weight: 600;
}

.harness-setup-rows__default {
  margin-left: 6px;
  color: var(--accent);
  font-size: 10.5px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
}

.harness-setup-rows__sub {
  margin-top: 2px;
  color: var(--muted);
  font-size: 12.5px;
  overflow-wrap: anywhere;
}

.harness-setup-rows__sub--mono {
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
}

.harness-setup-rows__actions {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-left: auto;
}

.harness-setup-rows__btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  padding: 6px 11px;
  background: var(--main-bg);
  color: var(--text);
  font-size: 12.5px;
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
  transition: border-color var(--transition), opacity var(--transition);
}

.harness-setup-rows__btn:hover {
  border-color: color-mix(in srgb, var(--accent) 50%, transparent);
}

.harness-setup-rows__btn--primary {
  border-color: var(--accent);
  background: var(--accent);
  color: var(--primary-foreground);
}

.harness-setup-rows__btn--primary:hover {
  opacity: 0.9;
}

.harness-setup-rows__btn:disabled {
  cursor: not-allowed;
  opacity: 0.5;
}

.harness-setup-rows__notes {
  display: grid;
  gap: 3px;
  margin: 0 0 0 46px;
  padding-left: 16px;
  color: var(--muted);
  font-size: 12px;
  list-style: disc;
}

.harness-setup-rows__choices {
  display: grid;
  gap: 6px;
  margin-left: 46px;
}

.harness-setup-rows__choices-title {
  color: var(--text);
  font-size: 12px;
  font-weight: 600;
}

.harness-setup-rows__choice {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  padding: 9px 11px;
  background: var(--main-bg);
  cursor: pointer;
  transition: border-color var(--transition);
}

.harness-setup-rows__choice:hover,
.harness-setup-rows__choice--picked {
  border-color: color-mix(in srgb, var(--accent) 55%, transparent);
}

.harness-setup-rows__choice input {
  flex: none;
  margin-top: 2px;
  accent-color: var(--accent);
}

.harness-setup-rows__choice-text {
  display: grid;
  gap: 2px;
  min-width: 0;
}

.harness-setup-rows__choice-label {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  color: var(--text);
  font-size: 12.5px;
  font-weight: 600;
}

.harness-setup-rows__recommended {
  border: 1px solid color-mix(in srgb, var(--accent) 30%, transparent);
  border-radius: 999px;
  padding: 0 7px;
  background: color-mix(in srgb, var(--accent) 10%, transparent);
  color: var(--accent);
  font-size: 10.5px;
  font-weight: 500;
}

.harness-setup-rows__choice-description {
  color: var(--muted);
  font-size: 12px;
  overflow-wrap: anywhere;
}

.harness-setup-rows__terminal-wrap {
  display: grid;
  gap: 10px;
}

.harness-setup-rows__terminal {
  overflow: hidden;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--main-bg);
}

.harness-setup-rows__terminal-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  border-bottom: 1px solid var(--border);
  padding: 6px 10px;
  color: var(--muted);
  font-size: 11.5px;
}

.harness-setup-rows__terminal-bar button {
  display: inline-flex;
  border: 0;
  border-radius: 4px;
  padding: 2px;
  background: none;
  color: var(--muted);
  cursor: pointer;
}

.harness-setup-rows__terminal-bar button:hover {
  color: var(--text);
}

/* TerminalView fills the nearest positioned box. */
.harness-setup-rows__terminal-body {
  position: relative;
  height: 220px;
  overflow: hidden;
}

.harness-setup-rows__starting {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 12px;
  color: var(--muted);
  font-size: 12.5px;
}

.harness-setup-rows__hint {
  border-top: 1px solid var(--border);
  padding: 8px 12px;
  color: var(--muted);
  font-size: 12px;
}

.harness-setup-rows__found {
  display: flex;
  align-items: center;
  gap: 8px;
  border: 1px solid color-mix(in srgb, var(--running) 30%, transparent);
  border-radius: var(--radius-btn);
  padding: 8px 10px;
  background: color-mix(in srgb, var(--running) 10%, transparent);
  color: var(--text);
  font-size: 12.5px;
  overflow-wrap: anywhere;
}

.harness-setup-rows__found svg {
  flex: none;
  color: var(--running);
}

.harness-setup-rows__failure {
  display: grid;
  gap: 8px;
  border: 1px solid color-mix(in srgb, var(--error) 30%, transparent);
  border-radius: var(--radius-btn);
  padding: 10px 12px;
  background: color-mix(in srgb, var(--error) 8%, transparent);
  color: var(--text);
  font-size: 12.5px;
}

.harness-setup-rows__copy {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}

.harness-setup-rows__copy code {
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 5px 8px;
  background: var(--card-bg);
  font-family: var(--font-mono-stack);
  font-size: 12px;
  overflow-wrap: anywhere;
}
</style>
