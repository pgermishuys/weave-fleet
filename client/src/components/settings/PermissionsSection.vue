<script setup lang="ts">
import { computed } from "vue";
import { Clock, Info } from "lucide-vue-next";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";
import {
  DEFAULT_PERMISSION_LEVEL,
  PERMISSION_LEVEL_KEY,
  PERMISSION_LEVEL_NAMES,
  PERMISSION_UNATTENDED_KEY,
  harnessPermissionKey,
  toPermissionLevel,
  toUnattendedPermission,
  type PermissionLevel,
  type UnattendedPermission,
} from "@/lib/permissions";
import { usePreferencesStore } from "@/stores/preferences";

const preferencesStore = usePreferencesStore();
preferencesStore.ensureLoaded();

interface LevelOption {
  id: PermissionLevel;
  description: string;
  /** What runs without asking, and what asks, in the order read · edit · shell · web. */
  runs: [string, boolean][];
}

const LEVELS: readonly LevelOption[] = [
  {
    id: "ask",
    description: "Reading and searching run. Anything that changes files, runs commands or goes online asks first.",
    runs: [["read", true], ["edit", false], ["shell", false], ["web", false]],
  },
  {
    id: "edits",
    description: "File edits run as well. Commands, web access and other tools ask first.",
    runs: [["read", true], ["edit", true], ["shell", false], ["web", false]],
  },
  {
    id: "all",
    description: "Nothing asks. Use it in a sandbox or a throwaway worktree.",
    runs: [["read", true], ["edit", true], ["shell", true], ["web", true]],
  },
];

const { harnesses } = useEnabledHarnesses();

/** Every harness Fleet knows, with what it hands each at the level, or null when it can't hand one a level. */
const harnessRows = computed(() =>
  harnesses.value.map((harness) => ({
    type: harness.type,
    name: harness.displayName,
    handed: harness.capabilities.supportsPermissionLevels === true
      ? harness.presentation?.permissionModes ?? null
      : null,
    supported: harness.capabilities.supportsPermissionLevels === true,
  })),
);

const defaultLevel = computed<PermissionLevel>(
  () => toPermissionLevel(preferencesStore.preferences[PERMISSION_LEVEL_KEY]) ?? DEFAULT_PERMISSION_LEVEL,
);

/** A harness's own level, or null when it uses the default. */
function harnessLevel(type: string): PermissionLevel | null {
  return toPermissionLevel(preferencesStore.preferences[harnessPermissionKey(type)]);
}

function effectiveLevel(type: string): PermissionLevel {
  return harnessLevel(type) ?? defaultLevel.value;
}

const unattended = computed<UnattendedPermission>(
  () => toUnattendedPermission(preferencesStore.preferences[PERMISSION_UNATTENDED_KEY]),
);

const UNATTENDED_NOTES: Record<UnattendedPermission, string> = {
  all: "Nobody is there to answer, so these runs ask about nothing.",
  same: "A run stops at its first ask and shows Needs you until you answer.",
  deny: "Anything that would ask is refused, and the agent is told why.",
};

function setDefault(level: PermissionLevel): void {
  void preferencesStore.set(PERMISSION_LEVEL_KEY, level);
}

function setHarness(type: string, value: string): void {
  // "default" clears the harness's own level; the preferences API keeps strings, so an empty one means unset.
  void preferencesStore.set(harnessPermissionKey(type), value === "default" ? "" : value);
}

function setUnattended(value: string): void {
  void preferencesStore.set(PERMISSION_UNATTENDED_KEY, value);
}

function onLevelKeydown(event: KeyboardEvent, index: number): void {
  const step = event.key === "ArrowRight" || event.key === "ArrowDown" ? 1 : event.key === "ArrowLeft" || event.key === "ArrowUp" ? -1 : 0;
  if (step === 0) return;
  event.preventDefault();
  const next = LEVELS[(index + step + LEVELS.length) % LEVELS.length];
  setDefault(next.id);
  (event.currentTarget as HTMLElement).parentElement?.querySelectorAll<HTMLElement>("[role=radio]")[LEVELS.indexOf(next)]?.focus();
}
</script>

<template>
  <section
    class="rounded-card border border-border bg-card-bg p-6 shadow-sm"
    data-testid="permissions-section"
  >
    <div class="flex flex-col gap-1">
      <h2 class="text-lg font-semibold text-text">
        Permissions
      </h2>
      <p class="text-sm text-muted">
        Choose what an agent can do without asking. When it needs your OK, the session shows
        <span class="permissions-needs"><StatusGlyph status="waiting_input" /> Needs you</span>
        and the ask waits in the conversation. Changes apply from each session's next message.
      </p>
    </div>

    <div class="mt-5 grid gap-2">
      <div class="flex flex-wrap items-baseline gap-2">
        <h3 class="text-sm font-semibold text-text">
          Default
        </h3>
        <span class="text-xs text-muted">Used by every harness unless you pick something else below.</span>
      </div>
      <div
        class="permissions-levels"
        role="radiogroup"
        aria-label="Default permission level"
      >
        <button
          v-for="(level, index) in LEVELS"
          :key="level.id"
          type="button"
          role="radio"
          class="permissions-level"
          :aria-checked="defaultLevel === level.id"
          :tabindex="defaultLevel === level.id ? 0 : -1"
          :data-testid="`permission-level-${level.id}`"
          @click="setDefault(level.id)"
          @keydown="onLevelKeydown($event, index)"
        >
          <span class="permissions-level__name">
            <span
              class="permissions-level__radio"
              aria-hidden="true"
            />
            {{ PERMISSION_LEVEL_NAMES[level.id] }}
          </span>
          <span class="permissions-level__desc">{{ level.description }}</span>
          <span
            class="permissions-level__runs"
            aria-hidden="true"
          >
            <span
              v-for="[name, runs] in level.runs"
              :key="name"
              :class="runs ? 'permissions-chip--runs' : 'permissions-chip--asks'"
            >{{ name }}</span>
          </span>
        </button>
      </div>
    </div>

    <div class="mt-5 grid gap-2">
      <div class="flex flex-wrap items-baseline gap-2">
        <h3 class="text-sm font-semibold text-text">
          Each harness
        </h3>
        <span class="text-xs text-muted">What Fleet hands the harness is shown under its name.</span>
      </div>
      <div class="permissions-rows">
        <div
          v-for="harness in harnessRows"
          :key="harness.type"
          class="permissions-row"
        >
          <span class="permissions-row__name">{{ harness.name }}</span>
          <span
            v-if="!harness.supported"
            class="permissions-row__handed permissions-row__handed--plain"
            :data-testid="`permission-handed-${harness.type}`"
          >Fleet can't hand {{ harness.name }} a permission level yet.</span>
          <span
            v-else
            class="permissions-row__handed"
            :data-testid="`permission-handed-${harness.type}`"
          >{{ harness.handed?.[effectiveLevel(harness.type)] ?? PERMISSION_LEVEL_NAMES[effectiveLevel(harness.type)] }}</span>
          <select
            v-if="harness.supported"
            :id="`permission-harness-${harness.type}`"
            class="permissions-select"
            :aria-label="`${harness.name} permissions`"
            :value="harnessLevel(harness.type) ?? 'default'"
            @change="setHarness(harness.type, ($event.target as HTMLSelectElement).value)"
          >
            <option value="default">
              Default ({{ PERMISSION_LEVEL_NAMES[defaultLevel] }})
            </option>
            <option
              v-for="level in LEVELS"
              :key="level.id"
              :value="level.id"
            >
              {{ PERMISSION_LEVEL_NAMES[level.id] }}
            </option>
          </select>
        </div>
      </div>
    </div>

    <div class="mt-5 grid gap-2">
      <div class="flex flex-wrap items-baseline gap-2">
        <h3 class="text-sm font-semibold text-text">
          Runs nobody is watching
        </h3>
        <span class="text-xs text-muted">Automations and workflow steps Fleet finishes.</span>
      </div>
      <div class="permissions-rows">
        <div class="permissions-row">
          <span class="permissions-row__name">
            <Clock
              :size="14"
              class="text-muted"
              aria-hidden="true"
            />
            Unattended runs
          </span>
          <span class="permissions-row__handed permissions-row__handed--plain">{{ UNATTENDED_NOTES[unattended] }}</span>
          <select
            id="permission-unattended"
            class="permissions-select"
            aria-label="Unattended runs"
            :value="unattended"
            @change="setUnattended(($event.target as HTMLSelectElement).value)"
          >
            <option value="all">
              Allow everything
            </option>
            <option value="same">
              Same as sessions (a run waits for you)
            </option>
            <option value="deny">
              Deny anything that would ask
            </option>
          </select>
        </div>
      </div>
    </div>

    <p class="permissions-fine mt-5">
      <Info
        :size="13"
        aria-hidden="true"
      />
      <span>
        <b>Don't ask again</b> lasts for the rest of the session, subagents included. A subagent's asks show on the
        session it works for.
      </span>
    </p>
  </section>
</template>

<style scoped>
.permissions-needs {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  color: var(--status-waiting);
  font-weight: 600;
}

.permissions-levels {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 8px;
}

.permissions-level {
  display: grid;
  align-content: start;
  gap: 6px;
  padding: 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--main-bg);
  color: var(--text);
  font: inherit;
  text-align: left;
  cursor: pointer;
  transition: border-color var(--transition), background var(--transition);
}

.permissions-level:hover {
  border-color: color-mix(in srgb, var(--accent) 35%, var(--border));
}

.permissions-level[aria-checked="true"] {
  border-color: var(--accent);
  background: var(--accent-dim);
}

.permissions-level:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.permissions-level__name {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 13.5px;
  font-weight: 600;
}

.permissions-level__radio {
  display: grid;
  place-items: center;
  width: 14px;
  height: 14px;
  flex-shrink: 0;
  border: 1.5px solid color-mix(in srgb, var(--text) 25%, transparent);
  border-radius: 50%;
}

.permissions-level[aria-checked="true"] .permissions-level__radio {
  border-color: var(--accent);
}

.permissions-level[aria-checked="true"] .permissions-level__radio::after {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--accent);
  content: "";
}

.permissions-level__desc {
  color: var(--muted);
  font-size: 12px;
  line-height: 1.45;
}

.permissions-level__runs {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  margin-top: 2px;
}

.permissions-level__runs > span {
  padding: 0 6px;
  border-radius: 999px;
  font-size: 11px;
}

.permissions-chip--runs {
  background: color-mix(in srgb, var(--running) 12%, transparent);
  color: var(--running);
}

.permissions-chip--asks {
  background: color-mix(in srgb, var(--status-waiting) 14%, transparent);
  color: var(--status-waiting);
}

.permissions-rows {
  overflow: hidden;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
}

.permissions-row {
  display: grid;
  grid-template-columns: 150px minmax(0, 1fr) auto;
  align-items: center;
  gap: 4px 14px;
  padding: 11px 14px;
  background: var(--main-bg);
}

.permissions-row + .permissions-row {
  border-top: 1px solid var(--border);
}

.permissions-row__name {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--text);
  font-size: 13px;
  font-weight: 500;
}

.permissions-row__handed {
  min-width: 0;
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
}

.permissions-row__handed--plain {
  font-family: inherit;
  font-size: 12px;
}

.permissions-select {
  height: 30px;
  padding: 0 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
  font: inherit;
  font-size: 12.5px;
  cursor: pointer;
}

.permissions-select:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.permissions-fine {
  display: flex;
  gap: 8px;
  margin-bottom: 0;
  color: var(--muted);
  font-size: 12px;
  line-height: 1.5;
}

.permissions-fine > svg {
  flex-shrink: 0;
  margin-top: 3px;
  color: var(--accent);
}

.permissions-fine b {
  color: var(--text);
  font-weight: 600;
}

@media (max-width: 860px) {
  .permissions-levels {
    grid-template-columns: 1fr;
  }

  .permissions-row {
    grid-template-columns: minmax(0, 1fr) auto;
  }

  .permissions-row__handed {
    grid-column: 1 / -1;
    grid-row: 2;
  }
}
</style>
