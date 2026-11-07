<script setup lang="ts">
import { computed, onMounted, onUnmounted, shallowRef } from "vue";
import { Info } from "lucide-vue-next";
import type { HarnessInfo } from "@/api/client";
import BrowserSection from "@/components/settings/BrowserSection.vue";
import SettingSegments from "@/components/settings/SettingSegments.vue";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import { useAgentBrowserSettings } from "@/composables/use-agent-browser-settings";
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";
import { useHarnessPermissions } from "@/composables/use-harness-permissions";
import { usePreferenceSwitch } from "@/composables/use-preference-switch";
import { useSettingsNav } from "@/composables/use-settings-nav";
import {
  DEFAULT_PERMISSION_LEVEL,
  DEFAULT_UNATTENDED_PERMISSION,
  PERMISSION_LEVEL_KEY,
  PERMISSION_UNATTENDED_KEY,
  levelWith,
  reachOf,
  toUnattendedPermission,
  type LevelReach,
  type Reach,
  type UnattendedPermission,
} from "@/lib/permissions";
import { useMachineTarget } from "@/lib/machine-target";
import { SESSION_MESSAGES_PREFERENCE_KEY } from "@/lib/session-messages";
import { agentWorkLevelName, useMachinesStore, type MachineAgentWork } from "@/stores/machines";
import { usePreferencesStore } from "@/stores/preferences";

/**
 * Settings → What agents can do: every "can agents…?" answer on one page. Each row has one setting, says which value is
 * Fleet's default, and lists its exceptions under it: a harness at a level of its own, a machine set to Always or Never.
 * Edit files and Run commands are two views of one level (ask · edits · all); the server hands every live session a
 * changed level at once, so it holds from the session's next tool call.
 */

// These are the settings of the machine Settings shows.
const target = useMachineTarget();
const preferencesStore = usePreferencesStore(target);
preferencesStore.ensureLoaded();

type Row = keyof LevelReach;

const REACH: readonly { value: Reach; label: string }[] = [
  { value: "allowed", label: "Allowed" },
  { value: "ask", label: "Ask" },
];
const REACH_NAMES: Record<Reach, string> = { allowed: "Allowed", ask: "Ask" };
const FLEET_DEFAULT_REACH = reachOf(DEFAULT_PERMISSION_LEVEL);

// The harnesses that can start a session here: installed and turned on.
const { enabledHarnesses: harnesses } = useEnabledHarnesses();
const { defaultLevel, effectiveLevel, supportsLevels, handedText, setHarnessLevel } = useHarnessPermissions();
const { settings: browser, load: loadBrowser } = useAgentBrowserSettings();
const { setActiveSection } = useSettingsNav();

const defaultReach = computed(() => reachOf(defaultLevel.value));
const leveled = computed(() => harnesses.value.filter((harness) => supportsLevels(harness)));
/** Harnesses Fleet can't hand a level: they ask, or don't, as their own settings say. */
const unleveled = computed(() => harnesses.value.filter((harness) => !supportsLevels(harness)));

function harnessReach(harness: HarnessInfo, row: Row): Reach {
  return reachOf(effectiveLevel(harness.type))[row];
}

/** The harnesses whose level differs from the default on this row: its exceptions. */
function exceptionsOn(row: Row): HarnessInfo[] {
  return leveled.value.filter((harness) => harnessReach(harness, row) !== defaultReach.value[row]);
}

/** The harnesses that could be made an exception on this row: those that follow the default on it. */
function candidatesOn(row: Row): HarnessInfo[] {
  return leveled.value.filter((harness) => harnessReach(harness, row) === defaultReach.value[row]);
}

function opposite(value: Reach): Reach {
  return value === "allowed" ? "ask" : "allowed";
}

function setDefault(row: Row, value: Reach): void {
  void preferencesStore.set(PERMISSION_LEVEL_KEY, levelWith(defaultLevel.value, row, value));
}

/** A harness's own level after one row changes; back at the default, it follows the default again. */
function setHarness(type: string, row: Row, value: Reach): void {
  const level = levelWith(effectiveLevel(type), row, value);
  setHarnessLevel(type, level === defaultLevel.value ? "default" : level);
}

function addHarnessException(row: Row, event: Event): void {
  const select = event.target as HTMLSelectElement;
  const type = select.value;
  select.value = "";
  if (type) setHarness(type, row, opposite(defaultReach.value[row]));
}

function defaultTag(isDefault: boolean, label: string): string {
  return isDefault ? "default" : `default: ${label}`;
}

// ── The browser, which Run commands covers ───────────────────────────────────
const browserHarnesses = computed(() => harnesses.value.filter((harness) => harness.capabilities?.supportsAgentBrowser === true));
const browserChip = computed(() => {
  if (!browser.value.enabled) return "Off";
  const without = harnesses.value.filter((harness) => !browserHarnesses.value.includes(harness));
  return without.length > 0 && browserHarnesses.value.length > 0
    ? `On, not in ${without.map((harness) => harness.displayName).join(", ")}`
    : browserHarnesses.value.length > 0 ? "On" : "No harness here can use it";
});

// ── Messages between sessions ────────────────────────────────────────────────
type MessagesChoice = "allowed" | "off";
const MESSAGES: readonly { value: MessagesChoice; label: string }[] = [
  { value: "allowed", label: "Allowed" },
  { value: "off", label: "Off" },
];
const sessionMessages = usePreferenceSwitch(SESSION_MESSAGES_PREFERENCE_KEY);
const messagesChoice = computed<MessagesChoice>(() => (sessionMessages.enabled.value ? "allowed" : "off"));

function setMessages(value: MessagesChoice): void {
  if (value !== messagesChoice.value) void sessionMessages.toggle();
}

// ── Other machines ───────────────────────────────────────────────────────────
const MACHINE_CHOICES: readonly { value: MachineAgentWork; label: string }[] = [
  { value: "ask", label: "Ask" },
  { value: "always", label: "Always" },
  { value: "never", label: "Never" },
];
const machines = useMachinesStore();
const machineExceptions = computed(() => machines.agentWorkEntries.filter((entry) => machines.agentWork[entry.key] !== "ask"));
const machineCandidates = computed(() => machines.agentWorkEntries.filter((entry) => machines.agentWork[entry.key] === "ask"));
// The machine Settings shows, by name (the machine list is the live machine's: its own entries only exist there).
const shownName = computed(() => machines.entries.find((entry) => entry.key === target.key)?.name ?? "this machine");
const machineError = shallowRef<string | null>(null);

async function setMachine(key: string, value: MachineAgentWork): Promise<void> {
  machineError.value = null;
  try {
    await machines.setAgentWork(key, value);
  } catch (error) {
    machineError.value = error instanceof Error ? error.message : String(error);
  }
}

function addMachineException(event: Event): void {
  const select = event.target as HTMLSelectElement;
  const [key, value] = select.value.split("|");
  select.value = "";
  if (key && value) void setMachine(key, value as MachineAgentWork);
}

// ── When nobody is watching ──────────────────────────────────────────────────
const UNATTENDED: readonly { value: UnattendedPermission; label: string }[] = [
  { value: "all", label: "Allow" },
  { value: "same", label: "Stop & wait" },
  { value: "deny", label: "Refuse" },
];
const UNATTENDED_NOTES: Record<UnattendedPermission, string> = {
  all: "Nobody is there to answer, so these runs ask about nothing.",
  same: "A run stops at its first ask and shows Needs you until you answer.",
  deny: "Anything that would ask is refused, and the agent is told why.",
};
const unattended = computed<UnattendedPermission>(
  () => toUnattendedPermission(preferencesStore.preferences[PERMISSION_UNATTENDED_KEY]),
);

function setUnattended(value: UnattendedPermission): void {
  void preferencesStore.set(PERMISSION_UNATTENDED_KEY, value);
}

let stopPolling: (() => void) | null = null;
onMounted(() => {
  void loadBrowser();
  // The list of machines agents may send work to is the live machine's: another machine's page doesn't ask for it.
  if (!target.isLive) return;
  void machines.loadHome();
  // An answer in a conversation ("Always for …") may have changed a machine's setting since the list was read.
  void machines.syncFromServer();
  stopPolling = machines.startPolling();
});
onUnmounted(() => stopPolling?.());
</script>

<template>
  <div
    class="grid gap-6"
    data-testid="permissions-section"
  >
    <p class="px-1 text-sm text-muted">
      For every harness on this machine. When an agent needs your OK, the session shows
      <span class="reach-needs"><StatusGlyph status="waiting_input" /> Needs you</span>
      and the ask waits in the conversation. A change reaches running sessions on their next tool call.
    </p>

    <div
      class="reach-rows"
      data-testid="reach-rows"
    >
      <!-- Edit files and Run commands: the level, a row each -->
      <section
        v-for="row in (['edits', 'commands'] as const)"
        :key="row"
        class="reach-row"
        :data-setting-row="row === 'edits' ? 'edit-files' : 'run-commands'"
        :data-testid="`reach-row-${row}`"
      >
        <div class="reach-row__head">
          <div class="min-w-0">
            <h3 class="reach-row__title">
              {{ row === "edits" ? "Edit files" : "Run commands & go online" }}
              <span class="reach-tag">{{ defaultTag(defaultReach[row] === FLEET_DEFAULT_REACH[row], REACH_NAMES[FLEET_DEFAULT_REACH[row]]) }}</span>
            </h3>
            <p class="reach-row__summary">
              {{ row === "edits" ? "Change and create files in the session's folder or worktree." : "The shell, web fetch, the browser and other tools." }}
            </p>
          </div>
          <SettingSegments
            :options="REACH"
            :value="defaultReach[row]"
            :label="row === 'edits' ? 'Edit files' : 'Run commands and go online'"
            :testid="`reach-${row}`"
            @pick="setDefault(row, $event)"
          />
        </div>
        <div class="reach-row__exceptions">
          <label
            v-for="harness in exceptionsOn(row)"
            :key="harness.type"
            class="reach-chip"
            :title="handedText(harness)"
            :data-testid="`reach-exception-${row}-${harness.type}`"
          >
            <span class="reach-chip__name">{{ harness.displayName }}</span>
            <select
              class="reach-chip__select"
              :aria-label="`${harness.displayName}: ${row === 'edits' ? 'edit files' : 'run commands'}`"
              :value="harnessReach(harness, row)"
              @change="setHarness(harness.type, row, ($event.target as HTMLSelectElement).value as Reach)"
            >
              <option
                v-for="option in REACH"
                :key="option.value"
                :value="option.value"
              >{{ option.label }}{{ option.value === defaultReach[row] ? " (as the default)" : "" }}</option>
            </select>
          </label>
          <span
            v-for="harness in unleveled"
            :key="harness.type"
            class="reach-chip reach-chip--fixed"
            :data-testid="`reach-unleveled-${row}-${harness.type}`"
          ><span class="reach-chip__name">{{ harness.displayName }}</span> its own settings: Fleet can't set it</span>
          <button
            v-if="row === 'commands'"
            type="button"
            class="reach-chip"
            data-testid="reach-browser-chip"
            @click="setActiveSection('permissions', 'browser')"
          ><span class="reach-chip__name">Browser</span> {{ browserChip }}</button>
          <select
            v-if="candidatesOn(row).length > 0"
            class="reach-add"
            :aria-label="`Add an exception to ${row === 'edits' ? 'Edit files' : 'Run commands'}`"
            :data-testid="`reach-add-${row}`"
            @change="addHarnessException(row, $event)"
          >
            <option value="">
              + exception
            </option>
            <option
              v-for="harness in candidatesOn(row)"
              :key="harness.type"
              :value="harness.type"
            >{{ harness.displayName }} · {{ REACH_NAMES[opposite(defaultReach[row])] }}</option>
          </select>
        </div>
        <p
          v-if="row === 'commands'"
          class="reach-row__note"
        >
          A command can change files too, so these go together: Edit files Ask makes commands ask, and Run commands
          Allowed allows edits.
        </p>
      </section>

      <!-- Messages between sessions -->
      <section
        class="reach-row"
        data-setting-row="session-messages"
        data-testid="reach-row-messages"
      >
        <div class="reach-row__head">
          <div class="min-w-0">
            <h3 class="reach-row__title">
              Message other sessions
              <span class="reach-tag">{{ defaultTag(messagesChoice === "off", "Off") }}</span>
            </h3>
            <p class="reach-row__summary">
              An agent asks or tells another session something with <code>fleet_message</code>. The message says which
              session sent it, and the agent there treats it as a teammate's request, not yours.
            </p>
          </div>
          <SettingSegments
            :options="MESSAGES"
            :value="messagesChoice"
            label="Message other sessions"
            testid="reach-messages"
            :disabled="preferencesStore.isLoading || sessionMessages.saving.value"
            @pick="setMessages"
          />
        </div>
        <p class="reach-row__note">
          Applies to sessions started afterwards.
        </p>
      </section>

      <!-- Send work to other machines -->
      <section
        class="reach-row"
        data-setting-row="other-machines"
        data-testid="reach-row-machines"
      >
        <div class="reach-row__head">
          <div class="min-w-0">
            <h3 class="reach-row__title">
              Send work to other machines
              <span class="reach-tag">default</span>
            </h3>
            <p class="reach-row__summary">
              Start a session on another machine and give it a task. The agent asks in the conversation first, and the
              work runs under that machine's own level.
            </p>
          </div>
          <span
            class="reach-fixed"
            title="Every machine starts at Ask. Set one to Always or Never below."
          >Ask</span>
        </div>
        <p
          v-if="!target.isLive"
          class="reach-row__note"
          data-testid="reach-machines-live-only"
        >
          Which machines {{ shownName }} may send work to isn't shown here yet. {{ machines.live.name }}'s list is on its own page.
        </p>
        <div
          v-else-if="machines.agentWorkEntries.length > 0"
          class="reach-row__exceptions"
        >
          <label
            v-for="entry in machineExceptions"
            :key="entry.key"
            class="reach-chip"
            :data-testid="`reach-machine-${entry.key}`"
          >
            <span class="reach-chip__name">{{ entry.name }}</span>
            <select
              class="reach-chip__select"
              :aria-label="`${entry.name}: agents here can send it work`"
              :value="machines.agentWork[entry.key]"
              @change="setMachine(entry.key, ($event.target as HTMLSelectElement).value as MachineAgentWork)"
            >
              <option
                v-for="choice in MACHINE_CHOICES"
                :key="choice.value"
                :value="choice.value"
              >{{ choice.label }}</option>
            </select>
            <span class="reach-chip__more">· runs at {{ agentWorkLevelName(entry) }}</span>
          </label>
          <select
            v-if="machineCandidates.length > 0"
            class="reach-add"
            aria-label="Add an exception to Send work to other machines"
            data-testid="reach-add-machines"
            @change="addMachineException"
          >
            <option value="">
              + exception
            </option>
            <template
              v-for="entry in machineCandidates"
              :key="entry.key"
            >
              <option :value="`${entry.key}|always`">
                {{ entry.name }} · Always
              </option>
              <option :value="`${entry.key}|never`">
                {{ entry.name }} · Never
              </option>
            </template>
          </select>
        </div>
        <p
          v-else
          class="reach-row__note"
        >
          No other machines yet.
          <button
            type="button"
            class="reach-link"
            @click="setActiveSection('machines')"
          >Add one in Machines</button>
        </p>
        <p
          v-if="machineError"
          class="reach-row__note text-error"
          role="alert"
        >
          {{ machineError }}
        </p>
      </section>

      <!-- When nobody is watching -->
      <section
        class="reach-row"
        data-setting-row="unattended-runs"
        data-testid="reach-row-unattended"
      >
        <div class="reach-row__head">
          <div class="min-w-0">
            <h3 class="reach-row__title">
              When nobody is watching
              <span class="reach-tag">{{ defaultTag(unattended === DEFAULT_UNATTENDED_PERMISSION, "Allow") }}</span>
            </h3>
            <p class="reach-row__summary">
              Automations and workflow steps Fleet finishes can't answer an ask. {{ UNATTENDED_NOTES[unattended] }}
            </p>
          </div>
          <SettingSegments
            :options="UNATTENDED"
            :value="unattended"
            label="When nobody is watching"
            testid="reach-unattended"
            @pick="setUnattended"
          />
        </div>
      </section>
    </div>

    <div data-settings-anchor="browser">
      <BrowserSection />
    </div>

    <p class="reach-fine">
      <Info
        :size="13"
        aria-hidden="true"
      />
      <span>
        <b>Don't ask again</b> lasts for the rest of the session, subagents included. A subagent's asks show on the
        session it works for.
      </span>
    </p>
  </div>
</template>

<style scoped>
.reach-needs {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  color: var(--status-waiting);
  font-weight: 600;
}

.reach-rows {
  overflow: hidden;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
}

.reach-row {
  display: grid;
  gap: 8px;
  padding: 14px 16px;
}

.reach-row + .reach-row {
  border-top: 1px solid var(--border);
}

.reach-row__head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
}

.reach-row__title {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 8px;
  color: var(--text);
  font-size: 13.5px;
  font-weight: 600;
}

.reach-row__summary {
  margin-top: 2px;
  color: var(--muted);
  font-size: 12px;
  line-height: 1.45;
}

.reach-row__summary code {
  padding: 0 4px;
  border-radius: 4px;
  background: var(--main-bg);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
}

.reach-tag {
  color: var(--muted);
  font-size: 11px;
  font-weight: 400;
}

.reach-row__exceptions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}

.reach-chip {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 2px 4px 2px 10px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--main-bg);
  color: var(--muted);
  font: inherit;
  font-size: 12px;
}

button.reach-chip {
  padding-right: 10px;
  cursor: pointer;
}

button.reach-chip:hover {
  border-color: color-mix(in srgb, var(--accent) 35%, var(--border));
}

.reach-chip--fixed {
  padding-right: 10px;
  border-style: dashed;
}

.reach-chip__name {
  color: var(--text);
  font-weight: 500;
}

.reach-chip__more {
  padding-right: 6px;
}

.reach-chip__select,
.reach-add {
  field-sizing: content;
  border-radius: 999px;
  background: transparent;
  color: var(--text);
  font: inherit;
  font-size: 12px;
  cursor: pointer;
}

.reach-chip__select {
  padding: 1px 2px;
  font-weight: 600;
}

.reach-add {
  padding: 2px 10px;
  border: 1px dashed var(--border);
  color: var(--muted);
}

.reach-add:hover,
.reach-chip__select:hover {
  color: var(--text);
}

.reach-chip__select:focus-visible,
.reach-add:focus-visible,
button.reach-chip:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.reach-fixed {
  padding: 4px 11px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  color: var(--text);
  font-size: 12.5px;
  font-weight: 600;
}

.reach-row__note {
  color: var(--muted);
  font-size: 11.5px;
  line-height: 1.45;
}

.reach-link {
  color: var(--accent);
  font: inherit;
  text-decoration: underline;
  cursor: pointer;
}

.reach-fine {
  display: flex;
  gap: 8px;
  margin-bottom: 0;
  color: var(--muted);
  font-size: 12px;
  line-height: 1.5;
}

.reach-fine > svg {
  flex-shrink: 0;
  margin-top: 3px;
  color: var(--accent);
}

.reach-fine b {
  color: var(--text);
  font-weight: 600;
}

@media (max-width: 640px) {
  .reach-row__head {
    flex-direction: column;
  }
}
</style>
