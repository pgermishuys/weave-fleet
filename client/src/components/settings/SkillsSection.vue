<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { AlertCircle, Brain, Download, LoaderCircle, Trash2 } from "lucide-vue-next";
import { useSkills } from "@/composables/use-skills";

const buttonPrimaryClass = "inline-flex items-center justify-center gap-2 rounded-btn bg-primary px-3 py-2 text-sm font-medium text-white transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60";
const buttonDangerClass = "inline-flex items-center justify-center gap-2 rounded-btn border border-red-500/40 bg-red-500/10 px-3 py-2 text-sm font-medium text-red-300 transition-colors hover:bg-red-500/20 disabled:cursor-not-allowed disabled:opacity-60";
const inputClass = "w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors placeholder:text-muted focus:border-accent";

const { skills, isLoading, error, installSkill, removeSkill } = useSkills();

const installUrl = shallowRef("");
const isInstalling = shallowRef(false);
const removingSkillName = shallowRef<string | null>(null);
const formError = shallowRef<string | null>(null);

const hasSkills = computed(() => skills.value.length > 0);

async function submitInstall(): Promise<void> {
  const url = installUrl.value.trim();
  if (!url) {
    formError.value = "Skill URL is required.";
    return;
  }

  isInstalling.value = true;
  formError.value = null;

  try {
    await installSkill({ url });
    installUrl.value = "";
  } catch (installError) {
    formError.value = installError instanceof Error ? installError.message : "Failed to install skill.";
  } finally {
    isInstalling.value = false;
  }
}

async function handleRemove(skillName: string): Promise<void> {
  removingSkillName.value = skillName;

  try {
    await removeSkill(skillName);
  } finally {
    removingSkillName.value = null;
  }
}
</script>

<template>
  <section class="rounded-card border border-border bg-card-bg p-6 shadow-sm">
    <div class="flex flex-col gap-1">
      <h2 class="text-lg font-semibold text-text">
        Skills
      </h2>
      <p class="text-sm text-muted">
        Manage installed skills and add new ones from a remote URL.
      </p>
    </div>

    <div class="mt-5 rounded-card border border-border bg-main-bg p-4">
      <form
        class="space-y-3"
        @submit.prevent="submitInstall"
      >
        <div>
          <h3 class="text-sm font-semibold text-text">
            Install skill
          </h3>
          <p class="mt-1 text-xs text-muted">
            Paste a skill URL to install it into the local workspace.
          </p>
        </div>

        <label class="grid gap-1 text-sm text-text">
          <span class="text-xs font-medium uppercase tracking-wide text-muted">Skill URL</span>
          <input
            v-model="installUrl"
            type="url"
            :class="inputClass"
            placeholder="https://example.com/skills/my-skill"
            :disabled="isInstalling"
          >
        </label>

        <div
          v-if="formError"
          class="flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-200"
          role="alert"
        >
          <AlertCircle
            :size="16"
            class="mt-0.5 shrink-0"
            aria-hidden="true"
          />
          <span>{{ formError }}</span>
        </div>

        <button
          type="submit"
          :class="buttonPrimaryClass"
          :disabled="isInstalling"
        >
          <LoaderCircle
            v-if="isInstalling"
            :size="16"
            class="animate-spin"
            aria-hidden="true"
          />
          <Download
            v-else
            :size="16"
            aria-hidden="true"
          />
          <span>{{ isInstalling ? "Installing…" : "Install Skill" }}</span>
        </button>
      </form>
    </div>

    <div
      v-if="isLoading"
      class="mt-5 flex items-center gap-2 text-sm text-muted"
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
      class="mt-5 flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-200"
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
      class="mt-5 rounded-card border border-dashed border-border p-6 text-center"
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
        Install a skill to make it available to compatible agents.
      </p>
    </div>

    <div
      v-else
      class="mt-5 grid gap-3"
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
            </div>

            <p class="mt-2 text-sm text-muted">
              {{ skill.description }}
            </p>
            <p class="mt-3 break-all font-mono text-xs text-muted">
              {{ skill.path }}
            </p>
          </div>

          <button
            type="button"
            :class="buttonDangerClass"
            :disabled="removingSkillName === skill.name"
            @click="handleRemove(skill.name)"
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
            <span>{{ removingSkillName === skill.name ? "Removing…" : "Remove" }}</span>
          </button>
        </div>
      </article>
    </div>

    <div
      v-if="error && hasSkills"
      class="mt-4 flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-200"
      role="alert"
    >
      <AlertCircle
        :size="16"
        class="mt-0.5 shrink-0"
        aria-hidden="true"
      />
      <span>{{ error }}</span>
    </div>
  </section>
</template>
