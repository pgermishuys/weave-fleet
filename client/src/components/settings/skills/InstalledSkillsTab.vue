<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { AlertTriangle, Brain, RefreshCw, Trash2 } from "lucide-vue-next";
import { useSkills, type SkillRef } from "@/composables/use-skills";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { targetKey, targetLabel } from "@/lib/install-target";
import { EmptyState } from "@/components/ui/empty-state";
import { ErrorNote } from "@/components/ui/error-note";
import { Spinner } from "@/components/ui/spinner";

const { skills, isLoading, error, removeSkill, checkUpdate, updateSkill } = useSkills();

// Busy states are per install: the same skill can be installed globally and in repositories.
const removingKey = shallowRef<string | null>(null);
const updatingKey = shallowRef<string | null>(null);
const checkingUpdateKey = shallowRef<string | null>(null);
const confirmRemoveKey = shallowRef<string | null>(null);

const hasSkills = computed(() => skills.value.length > 0);

function keyOf(skill: SkillRef): string {
  return `${skill.name}|${targetKey(skill.target)}`;
}

async function handleCheckUpdate(skill: SkillRef): Promise<void> {
  checkingUpdateKey.value = keyOf(skill);
  try {
    await checkUpdate(skill);
  } finally {
    checkingUpdateKey.value = null;
  }
}

async function handleUpdate(skill: SkillRef): Promise<void> {
  updatingKey.value = keyOf(skill);
  try {
    await updateSkill(skill);
  } finally {
    updatingKey.value = null;
  }
}

async function handleRemove(skill: SkillRef): Promise<void> {
  removingKey.value = keyOf(skill);
  try {
    await removeSkill(skill);
    confirmRemoveKey.value = null;
  } catch {
    // The composable's error shows below the list.
  } finally {
    removingKey.value = null;
  }
}

function confirmRemove(skill: SkillRef): void {
  confirmRemoveKey.value = keyOf(skill);
}

function cancelRemove(): void {
  confirmRemoveKey.value = null;
}
</script>

<template>
  <div class="space-y-4">
    <div
      v-if="isLoading"
      class="flex items-center gap-2 text-sm text-muted"
    >
      <Spinner />
      <span>Loading skills…</span>
    </div>

    <ErrorNote
      v-else-if="error && !hasSkills"
      :message="error"
    />

    <EmptyState
      v-else-if="!hasSkills"
      :icon="Brain"
      title="No skills installed"
      description="Install a skill from the Catalog or Custom tab."
      bordered
    />

    <div
      v-else
      class="grid gap-3"
    >
      <article
        v-for="skill in skills"
        :key="keyOf(skill)"
        class="rounded-card border border-border bg-main-bg p-4"
      >
        <div class="flex flex-col gap-4 md:flex-row md:items-start md:justify-between">
          <div class="min-w-0 flex-1">
            <div class="flex items-center gap-2">
              <Brain
                :size="16"
                class="shrink-0 text-muted"
                aria-hidden="true"
              />
              <h3 class="truncate text-sm font-semibold text-text">
                {{ skill.name }}
              </h3>
              <Badge
                variant="outline"
                :title="skill.target.scope === 'project' ? skill.target.projectPath : 'Every session'"
              >
                {{ targetLabel(skill.target) }}
              </Badge>
              <Badge
                v-if="skill.updateAvailable"
                variant="default"
                class="ml-2"
              >
                Update Available
              </Badge>
              <Badge
                v-if="skill.source === 'Bundled'"
                variant="secondary"
              >
                Bundled
              </Badge>
            </div>

            <p
              v-if="skill.description"
              class="mt-2 text-sm text-muted"
            >
              {{ skill.description }}
            </p>
            <p class="mt-3 break-all font-mono text-xs text-muted">
              {{ skill.path || skill.repoUrl || skill.localPath }}
            </p>
            <p
              v-for="installedPath in skill.installedPaths"
              :key="installedPath"
              class="mt-1 break-all font-mono text-xs text-muted"
            >
              → {{ installedPath }}
            </p>

            <div
              v-if="skill.installedPaths.length === 0"
              class="mt-2 flex items-start gap-2 rounded-card border border-coral/30 bg-coral/10 px-2 py-1 text-xs text-coral-text"
            >
              <AlertTriangle
                :size="12"
                class="mt-0.5 shrink-0"
                aria-hidden="true"
              />
              <span>Not in any folder a harness reads. Remove it and install it again.</span>
            </div>

            <ErrorNote
              v-if="skill.updateCheckError"
              :message="skill.updateCheckError"
              class="mt-2"
            />
          </div>

          <div class="flex flex-wrap gap-2">
            <Button
              v-if="skill.source === 'GitHub'"
              variant="outline"
              size="sm"
              :disabled="checkingUpdateKey === keyOf(skill) || updatingKey === keyOf(skill)"
              @click="handleCheckUpdate(skill)"
            >
              <Spinner v-if="checkingUpdateKey === keyOf(skill)" />
              <RefreshCw
                v-else
                :size="16"
                aria-hidden="true"
              />
              <span>{{ checkingUpdateKey === keyOf(skill) ? "Checking…" : "Check for updates" }}</span>
            </Button>

            <Button
              v-if="skill.updateAvailable"
              variant="default"
              size="sm"
              :disabled="updatingKey === keyOf(skill)"
              @click="handleUpdate(skill)"
            >
              <Spinner v-if="updatingKey === keyOf(skill)" />
              <RefreshCw
                v-else
                :size="16"
                aria-hidden="true"
              />
              <span>{{ updatingKey === keyOf(skill) ? "Updating…" : "Update" }}</span>
            </Button>

            <Button
              v-if="skill.source !== 'Bundled'"
              variant="destructive"
              size="sm"
              :disabled="removingKey === keyOf(skill)"
              @click="confirmRemoveKey === keyOf(skill) ? handleRemove(skill) : confirmRemove(skill)"
            >
              <Spinner v-if="removingKey === keyOf(skill)" />
              <Trash2
                v-else
                :size="16"
                aria-hidden="true"
              />
              <span>
                {{ removingKey === keyOf(skill) ? "Removing…" : confirmRemoveKey === keyOf(skill) ? "Confirm remove" : "Remove" }}
              </span>
            </Button>

            <Button
              v-if="confirmRemoveKey === keyOf(skill) && removingKey !== keyOf(skill)"
              variant="outline"
              size="sm"
              @click="cancelRemove"
            >
              Cancel
            </Button>
          </div>
        </div>
      </article>
    </div>

    <ErrorNote
      v-if="error && hasSkills"
      :message="error"
    />
  </div>
</template>
