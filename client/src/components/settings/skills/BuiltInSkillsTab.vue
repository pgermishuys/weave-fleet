<script setup lang="ts">
import { shallowRef } from "vue";
import { AlertCircle, LoaderCircle, Sparkles } from "lucide-vue-next";
import SkillDiff from "@/components/skills/SkillDiff.vue";
import SkillEditorDialog from "@/components/skills/SkillEditorDialog.vue";
import SkillHistoryDialog from "@/components/skills/SkillHistoryDialog.vue";
import { useBuiltInSkills } from "@/composables/use-built-in-skills";
import {
  getSkill,
  keepMySkillVersion,
  switchSkillVersion,
  versionLabel,
  type BuiltInSkillDetail,
} from "@/lib/skill-versions";
import { useBuiltInSkillsStore } from "@/stores/built-in-skills";

const { skills, isLoading, error, savingName, setEnabled } = useBuiltInSkills();
const store = useBuiltInSkillsStore();

const editing = shallowRef<string | null>(null);
const showingHistory = shallowRef<string | null>(null);
/** The skill whose Fleet change is open, with both of Fleet's versions. */
const fleetChange = shallowRef<BuiltInSkillDetail | null>(null);
const busyName = shallowRef<string | null>(null);
const actionError = shallowRef<string | null>(null);

async function run(name: string, action: () => Promise<BuiltInSkillDetail>): Promise<void> {
  if (busyName.value) return;
  busyName.value = name;
  actionError.value = null;
  try {
    const detail = await action();
    store.update(detail);
    if (fleetChange.value?.name === name) fleetChange.value = null;
  } catch (caught) {
    actionError.value = caught instanceof Error ? caught.message : `Couldn't change ${name}.`;
  } finally {
    busyName.value = null;
  }
}

async function toggleFleetChange(name: string): Promise<void> {
  if (fleetChange.value?.name === name) {
    fleetChange.value = null;
    return;
  }
  actionError.value = null;
  try {
    fleetChange.value = await getSkill(name);
  } catch (caught) {
    actionError.value = caught instanceof Error ? caught.message : `Couldn't load ${name}.`;
  }
}
</script>

<template>
  <div class="space-y-4">
    <p class="text-sm text-muted">
      Skills that come with Fleet. Each one is off until you turn it on, and sessions you start afterwards get it.
      Their names start with <code class="font-mono text-xs text-text">fleet-</code>, so they never take the place of
      a skill of your own. Edit one to make it work your way: your version replaces Fleet's in new sessions, and
      Fleet's updates never overwrite it.
    </p>

    <div
      v-if="isLoading && skills.length === 0"
      class="flex items-center gap-2 text-sm text-muted"
    >
      <LoaderCircle
        :size="16"
        class="animate-spin"
        aria-hidden="true"
      />
      <span>Loading built-in skills…</span>
    </div>

    <div
      v-else
      class="grid gap-3"
    >
      <article
        v-for="skill in skills"
        :key="skill.name"
        class="grid gap-3 rounded-card border border-border bg-main-bg p-4"
        :data-testid="`built-in-skill-${skill.name}`"
      >
        <div class="flex items-start justify-between gap-4">
          <div class="min-w-0">
            <div class="flex flex-wrap items-center gap-2">
              <Sparkles
                :size="16"
                class="shrink-0 text-muted"
                aria-hidden="true"
              />
              <h3 class="truncate font-mono text-sm font-semibold text-text">
                {{ skill.name }}
              </h3>
              <span
                class="skill-pill"
                :class="skill.version ? 'skill-pill--yours' : 'skill-pill--fleet'"
                data-testid="built-in-skill-version"
              >{{ versionLabel(skill.version) }}</span>
              <span
                v-if="skill.fleetChanged"
                class="skill-pill skill-pill--changed"
              >Fleet changed it</span>
            </div>
            <p class="mt-2 text-sm text-muted">
              {{ skill.description }}
            </p>
          </div>

          <div class="flex items-center gap-2">
            <LoaderCircle
              v-if="savingName === skill.name"
              :size="16"
              class="animate-spin text-muted"
              aria-hidden="true"
            />
            <button
              type="button"
              role="switch"
              :aria-checked="skill.enabled"
              :aria-label="`Use ${skill.name} in new sessions`"
              :disabled="savingName !== null"
              class="relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-accent focus:ring-offset-2 focus:ring-offset-main-bg disabled:cursor-not-allowed disabled:opacity-60"
              :class="skill.enabled ? 'bg-accent' : 'bg-border'"
              @click="setEnabled(skill.name, !skill.enabled)"
            >
              <span
                class="pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out"
                :class="skill.enabled ? 'translate-x-5' : 'translate-x-0'"
              />
            </button>
          </div>
        </div>

        <div
          v-if="skill.fleetChanged"
          class="skill-changed"
          data-testid="built-in-skill-fleet-changed"
        >
          <p>
            <strong>Fleet changed its version</strong> since you made yours. Sessions still use yours.
          </p>
          <div class="skill-links">
            <button
              type="button"
              @click="toggleFleetChange(skill.name)"
            >
              {{ fleetChange?.name === skill.name ? "Hide what changed" : "See what changed" }}
            </button>
            <button
              type="button"
              :disabled="busyName !== null"
              data-testid="built-in-skill-keep-mine"
              @click="run(skill.name, () => keepMySkillVersion(skill.name))"
            >
              Keep mine
            </button>
            <button
              type="button"
              :disabled="busyName !== null"
              @click="run(skill.name, () => switchSkillVersion(skill.name, null))"
            >
              Use Fleet's
            </button>
          </div>
          <SkillDiff
            v-if="fleetChange?.name === skill.name"
            :before="fleetChange.fleetBefore ?? ''"
            :after="fleetChange.fleetContent"
            label="What Fleet changed in its version"
          />
        </div>

        <div class="skill-links">
          <button
            type="button"
            :data-testid="`built-in-skill-edit-${skill.name}`"
            @click="editing = skill.name"
          >
            Edit
          </button>
          <button
            v-if="(skill.versionCount ?? 0) > 0"
            type="button"
            :data-testid="`built-in-skill-history-${skill.name}`"
            @click="showingHistory = skill.name"
          >
            History
          </button>
          <button
            v-if="skill.version && !skill.fleetChanged"
            type="button"
            :disabled="busyName !== null"
            @click="run(skill.name, () => switchSkillVersion(skill.name, null))"
          >
            Use Fleet's
          </button>
          <LoaderCircle
            v-if="busyName === skill.name"
            class="h-3.5 w-3.5 animate-spin text-muted"
            aria-hidden="true"
          />
        </div>
      </article>
    </div>

    <div
      v-if="error || actionError"
      class="flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-200"
      role="alert"
    >
      <AlertCircle
        :size="16"
        class="mt-0.5 shrink-0"
        aria-hidden="true"
      />
      <span>{{ actionError ?? error }}</span>
    </div>

    <SkillEditorDialog
      v-if="editing"
      :name="editing"
      :open="editing !== null"
      @update:open="(value) => { if (!value) editing = null }"
    />
    <SkillHistoryDialog
      v-if="showingHistory"
      :name="showingHistory"
      :open="showingHistory !== null"
      @update:open="(value) => { if (!value) showingHistory = null }"
    />
  </div>
</template>

<style scoped>
.skill-pill {
  display: inline-flex;
  align-items: center;
  padding: 0 7px;
  border-radius: 999px;
  font-size: 11px;
  font-weight: 600;
  white-space: nowrap;
}

.skill-pill--fleet {
  border: 1px solid var(--border);
  color: var(--muted);
}

.skill-pill--yours {
  background: var(--accent-dim);
  color: var(--accent);
}

.skill-pill--changed {
  background: color-mix(in srgb, var(--status-waiting) 14%, transparent);
  color: var(--status-waiting);
}

.skill-changed {
  display: grid;
  gap: 8px;
  padding: 10px 12px;
  border: 1px solid color-mix(in srgb, var(--status-waiting) 35%, transparent);
  border-radius: var(--radius-btn);
  background: color-mix(in srgb, var(--status-waiting) 7%, transparent);
  font-size: 13px;
}

.skill-links {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 16px;
  font-size: 13px;
}

.skill-links button {
  padding: 0;
  border: 0;
  background: none;
  color: var(--accent);
  font: inherit;
  font-weight: 500;
  cursor: pointer;
}

.skill-links button:hover {
  text-decoration: underline;
}

.skill-links button:disabled {
  cursor: default;
  opacity: 0.5;
}
</style>
