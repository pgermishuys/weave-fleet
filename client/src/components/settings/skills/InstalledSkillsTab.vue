<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { AlertCircle, Brain, LoaderCircle, RefreshCw, Trash2 } from "lucide-vue-next";
import { useSkills } from "@/composables/use-skills";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";

const { skills, isLoading, error, removeSkill, checkUpdate, updateSkill } = useSkills();

const removingSkillName = shallowRef<string | null>(null);
const updatingSkillName = shallowRef<string | null>(null);
const checkingUpdateSkillName = shallowRef<string | null>(null);
const confirmRemoveSkillName = shallowRef<string | null>(null);

const hasSkills = computed(() => skills.value.length > 0);

async function handleCheckUpdate(skillName: string): Promise<void> {
  checkingUpdateSkillName.value = skillName;
  try {
    await checkUpdate(skillName);
  } finally {
    checkingUpdateSkillName.value = null;
  }
}

async function handleUpdate(skillName: string): Promise<void> {
  updatingSkillName.value = skillName;
  try {
    await updateSkill(skillName);
  } finally {
    updatingSkillName.value = null;
  }
}

async function handleRemove(skillName: string): Promise<void> {
  removingSkillName.value = skillName;
  try {
    await removeSkill(skillName);
    confirmRemoveSkillName.value = null;
  } finally {
    removingSkillName.value = null;
  }
}

function confirmRemove(skillName: string): void {
  confirmRemoveSkillName.value = skillName;
}

function cancelRemove(): void {
  confirmRemoveSkillName.value = null;
}
</script>

<template>
  <div class="space-y-4">
    <div
      v-if="isLoading"
      class="flex items-center gap-2 text-sm text-muted"
    >
      <LoaderCircle
        :size="16"
        class="animate-spin"
        aria-hidden="true"
      />
      <span>Loading skills…</span>
    </div>

    <div
      v-else-if="error && !hasSkills"
      class="flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-200"
      role="alert"
    >
      <AlertCircle
        :size="16"
        class="mt-0.5 shrink-0"
        aria-hidden="true"
      />
      <span>{{ error }}</span>
    </div>

    <div
      v-else-if="!hasSkills"
      class="rounded-card border border-dashed border-border p-6 text-center"
    >
      <Brain
        :size="28"
        class="mx-auto text-muted"
        aria-hidden="true"
      />
      <p class="mt-3 text-sm font-medium text-text">
        No skills installed
      </p>
      <p class="mt-1 text-xs text-muted">
        Install a skill from the Catalog or Custom tab.
      </p>
    </div>

    <div
      v-else
      class="grid gap-3"
    >
      <article
        v-for="skill in skills"
        :key="skill.name"
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

            <div
              v-if="skill.updateCheckError"
              class="mt-2 flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-2 py-1 text-xs text-red-200"
            >
              <AlertCircle
                :size="12"
                class="mt-0.5 shrink-0"
                aria-hidden="true"
              />
              <span>{{ skill.updateCheckError }}</span>
            </div>
          </div>

          <div class="flex flex-wrap gap-2">
            <Button
              v-if="skill.source === 'GitHub'"
              variant="outline"
              size="sm"
              :disabled="checkingUpdateSkillName === skill.name || updatingSkillName === skill.name"
              @click="handleCheckUpdate(skill.name)"
            >
              <LoaderCircle
                v-if="checkingUpdateSkillName === skill.name"
                :size="16"
                class="animate-spin"
                aria-hidden="true"
              />
              <RefreshCw
                v-else
                :size="16"
                aria-hidden="true"
              />
              <span>{{ checkingUpdateSkillName === skill.name ? "Checking…" : "Check Update" }}</span>
            </Button>

            <Button
              v-if="skill.updateAvailable"
              variant="default"
              size="sm"
              :disabled="updatingSkillName === skill.name"
              @click="handleUpdate(skill.name)"
            >
              <LoaderCircle
                v-if="updatingSkillName === skill.name"
                :size="16"
                class="animate-spin"
                aria-hidden="true"
              />
              <RefreshCw
                v-else
                :size="16"
                aria-hidden="true"
              />
              <span>{{ updatingSkillName === skill.name ? "Updating…" : "Update" }}</span>
            </Button>

            <Button
              v-if="skill.source !== 'Bundled'"
              variant="destructive"
              size="sm"
              :disabled="removingSkillName === skill.name"
              @click="confirmRemoveSkillName === skill.name ? handleRemove(skill.name) : confirmRemove(skill.name)"
            >
              <LoaderCircle
                v-if="removingSkillName === skill.name"
                :size="16"
                class="animate-spin"
                aria-hidden="true"
              />
              <Trash2
                v-else
                :size="16"
                aria-hidden="true"
              />
              <span>
                {{ removingSkillName === skill.name ? "Removing…" : confirmRemoveSkillName === skill.name ? "Confirm Remove" : "Remove" }}
              </span>
            </Button>

            <Button
              v-if="confirmRemoveSkillName === skill.name && removingSkillName !== skill.name"
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

    <div
      v-if="error && hasSkills"
      class="flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-200"
      role="alert"
    >
      <AlertCircle
        :size="16"
        class="mt-0.5 shrink-0"
        aria-hidden="true"
      />
      <span>{{ error }}</span>
    </div>
  </div>
</template>
