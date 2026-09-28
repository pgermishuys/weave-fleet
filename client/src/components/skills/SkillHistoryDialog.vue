<script setup lang="ts">
import { shallowRef, watch } from "vue";
import { AlertCircle, LoaderCircle } from "lucide-vue-next";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import SkillDiff from "@/components/skills/SkillDiff.vue";
import { formatAbsoluteTimestamp, formatRelativeTime } from "@/lib/format-utils";
import {
  getSkill,
  readSkillVersion,
  switchSkillVersion,
  type BuiltInSkillDetail,
} from "@/lib/skill-versions";
import { useBuiltInSkillsStore } from "@/stores/built-in-skills";

/**
 * A skill's history: each of the user's versions with why it was made, newest first, then Fleet's. Any of them can be
 * the one sessions get, and each compares with Fleet's.
 */
const props = defineProps<{
  name: string;
}>();

const open = defineModel<boolean>("open", { required: true });

const store = useBuiltInSkillsStore();
const detail = shallowRef<BuiltInSkillDetail | null>(null);
const comparing = shallowRef<{ number: number; content: string } | null>(null);
const busy = shallowRef<number | "fleet" | null>(null);
const error = shallowRef<string | null>(null);

watch(open, async (isOpen) => {
  if (!isOpen) return;
  detail.value = null;
  comparing.value = null;
  error.value = null;
  try {
    detail.value = await getSkill(props.name);
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : `Couldn't load ${props.name}.`;
  }
}, { immediate: true });

async function use(version: number | null): Promise<void> {
  if (busy.value !== null) return;
  busy.value = version ?? "fleet";
  error.value = null;
  try {
    detail.value = await switchSkillVersion(props.name, version);
    store.update(detail.value);
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : `Couldn't switch ${props.name}.`;
  } finally {
    busy.value = null;
  }
}

async function compare(version: number): Promise<void> {
  if (comparing.value?.number === version) {
    comparing.value = null;
    return;
  }
  error.value = null;
  try {
    comparing.value = { number: version, content: await readSkillVersion(props.name, version) };
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : `Couldn't load version ${version}.`;
  }
}

function when(createdAt: string): string {
  return formatRelativeTime(createdAt);
}

function exactly(createdAt: string): string {
  return formatAbsoluteTimestamp(Date.parse(createdAt));
}
</script>

<template>
  <Dialog v-model:open="open">
    <DialogContent
      class="max-h-[88dvh] overflow-y-auto sm:max-w-2xl"
      data-testid="skill-history-dialog"
    >
      <DialogHeader>
        <DialogTitle>
          <span class="font-mono">{{ name }}</span> history
        </DialogTitle>
        <DialogDescription>
          Your versions, newest first, with why each one changed. Sessions you start use the one marked In use.
        </DialogDescription>
      </DialogHeader>

      <div
        v-if="!detail && !error"
        class="flex items-center gap-2 text-sm text-muted"
      >
        <LoaderCircle
          class="h-4 w-4 animate-spin"
          aria-hidden="true"
        />
        Loading the history…
      </div>

      <ol
        v-if="detail"
        class="skill-history"
      >
        <li
          v-for="version in detail.versions"
          :key="version.number"
          class="skill-history__row"
          :data-testid="`skill-version-${version.number}`"
        >
          <span class="skill-history__number">v{{ version.number }}</span>
          <div class="min-w-0 space-y-1">
            <p
              class="text-sm"
              :class="version.note ? 'text-text' : 'text-muted'"
            >
              {{ version.note ? `“${version.note}”` : "No note" }}
            </p>
            <p class="text-xs text-muted">
              <span :title="exactly(version.createdAt)">{{ when(version.createdAt) }}</span>
              <template v-if="version.sessionTitle">
                · from {{ version.sessionTitle }}
              </template>
            </p>
            <div class="skill-history__actions">
              <span
                v-if="version.active"
                class="skill-history__in-use"
              >In use</span>
              <button
                v-else
                type="button"
                :disabled="busy !== null"
                :data-testid="`skill-version-use-${version.number}`"
                @click="use(version.number)"
              >
                <LoaderCircle
                  v-if="busy === version.number"
                  class="h-3 w-3 animate-spin"
                />
                Use this version
              </button>
              <button
                type="button"
                @click="compare(version.number)"
              >
                {{ comparing?.number === version.number ? "Hide" : "Compare with Fleet's" }}
              </button>
            </div>
            <SkillDiff
              v-if="comparing?.number === version.number"
              :before="detail.fleetContent"
              :after="comparing.content"
              :label="`Fleet's → v${version.number}`"
            />
          </div>
        </li>
        <li class="skill-history__row">
          <span class="skill-history__number text-muted">Fleet</span>
          <div class="min-w-0 space-y-1">
            <p class="text-sm text-muted">
              Fleet's version, as this Fleet ships it.
            </p>
            <div class="skill-history__actions">
              <span
                v-if="detail.version === null"
                class="skill-history__in-use"
              >In use</span>
              <button
                v-else
                type="button"
                :disabled="busy !== null"
                data-testid="skill-version-use-fleet"
                @click="use(null)"
              >
                <LoaderCircle
                  v-if="busy === 'fleet'"
                  class="h-3 w-3 animate-spin"
                />
                Use Fleet's
              </button>
            </div>
          </div>
        </li>
      </ol>

      <div
        v-if="error"
        class="flex items-start gap-3 border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive"
        role="alert"
      >
        <AlertCircle class="mt-0.5 h-4 w-4 shrink-0" />
        <p>{{ error }}</p>
      </div>
    </DialogContent>
  </Dialog>
</template>

<style scoped>
.skill-history {
  display: grid;
  margin: 0;
  padding: 0;
  list-style: none;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
}

.skill-history__row {
  display: grid;
  grid-template-columns: 44px minmax(0, 1fr);
  gap: 12px;
  padding: 12px 14px;
}

.skill-history__row + .skill-history__row {
  border-top: 1px solid var(--border);
}

.skill-history__number {
  font-family: var(--font-mono-stack);
  font-size: 12.5px;
  font-weight: 600;
  line-height: 1.6;
}

.skill-history__actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 14px;
  font-size: 12.5px;
}

.skill-history__actions button {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 0;
  border: 0;
  background: none;
  color: var(--accent);
  font: inherit;
  cursor: pointer;
}

.skill-history__actions button:hover {
  text-decoration: underline;
}

.skill-history__actions button:disabled {
  cursor: default;
  opacity: 0.5;
}

.skill-history__in-use {
  padding: 0 7px;
  border-radius: 999px;
  background: var(--accent-dim);
  color: var(--accent);
  font-size: 11.5px;
  font-weight: 600;
}
</style>
