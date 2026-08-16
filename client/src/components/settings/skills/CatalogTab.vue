<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { AlertCircle, Brain, Download, LoaderCircle } from "lucide-vue-next";
import { useSkillCatalog } from "@/composables/use-skill-catalog";
import { useSkills } from "@/composables/use-skills";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import type { InstallSkillRequest } from "@/composables/use-skill-catalog";

const { catalog, isLoading, error, installSkill } = useSkillCatalog();
const { skills: installedSkills, fetchSkills } = useSkills();

const installingSkillName = shallowRef<string | null>(null);

const hasCatalog = computed(() => catalog.value.length > 0);

function isInstalled(skillName: string): boolean {
  return installedSkills.value.some((s) => s.name === skillName);
}

async function handleInstall(skillName: string): Promise<void> {
  const catalogEntry = catalog.value.find((s) => s.name === skillName);
  if (!catalogEntry) {
    return;
  }

  installingSkillName.value = skillName;

  try {
    const request: InstallSkillRequest = {
      name: catalogEntry.name,
      source: catalogEntry.source,
      repoUrl: catalogEntry.repoUrl ?? null,
      ref: catalogEntry.ref ?? null,
      localPath: catalogEntry.localPath ?? null,
    };

    await installSkill(request);
    await fetchSkills();
  } finally {
    installingSkillName.value = null;
  }
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
      <span>Loading catalog…</span>
    </div>

    <div
      v-else-if="error && !hasCatalog"
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
      v-else-if="!hasCatalog"
      class="rounded-card border border-dashed border-border p-6 text-center"
    >
      <Brain
        :size="28"
        class="mx-auto text-muted"
        aria-hidden="true"
      />
      <p class="mt-3 text-sm font-medium text-text">
        No skills in catalog
      </p>
      <p class="mt-1 text-xs text-muted">
        The skill catalog is empty or unavailable.
      </p>
    </div>

    <div
      v-else
      class="grid gap-3 sm:grid-cols-2"
    >
      <article
        v-for="skill in catalog"
        :key="skill.name"
        class="rounded-card border border-border bg-main-bg p-4"
      >
        <div class="flex flex-col gap-3">
          <div class="flex items-start gap-2">
            <Brain
              :size="16"
              class="mt-0.5 shrink-0 text-muted"
              aria-hidden="true"
            />
            <div class="min-w-0 flex-1">
              <h3 class="truncate text-sm font-semibold text-text">
                {{ skill.displayName || skill.name }}
              </h3>
              <p
                v-if="skill.author"
                class="mt-0.5 text-xs text-muted"
              >
                by {{ skill.author }}
              </p>
            </div>
          </div>

          <p
            v-if="skill.description"
            class="text-sm text-muted line-clamp-3"
          >
            {{ skill.description }}
          </p>

          <div
            v-if="skill.tags.length > 0"
            class="flex flex-wrap gap-1"
          >
            <Badge
              v-for="tag in skill.tags"
              :key="tag"
              variant="outline"
              class="text-xs"
            >
              {{ tag }}
            </Badge>
          </div>

          <div class="flex items-center justify-between gap-2 pt-2">
            <div class="flex items-center gap-2">
              <Badge
                v-if="skill.source === 'GitHub'"
                variant="secondary"
                class="text-xs"
              >
                GitHub
              </Badge>
              <Badge
                v-else-if="skill.source === 'Local'"
                variant="secondary"
                class="text-xs"
              >
                Local
              </Badge>
              <Badge
                v-else-if="skill.source === 'Bundled'"
                variant="secondary"
                class="text-xs"
              >
                Bundled
              </Badge>
              <span
                v-if="skill.version"
                class="text-xs text-muted"
              >
                v{{ skill.version }}
              </span>
            </div>

            <Button
              v-if="isInstalled(skill.name)"
              variant="outline"
              size="sm"
              disabled
            >
              Installed
            </Button>
            <Button
              v-else
              variant="default"
              size="sm"
              :disabled="installingSkillName === skill.name"
              @click="handleInstall(skill.name)"
            >
              <LoaderCircle
                v-if="installingSkillName === skill.name"
                :size="16"
                class="animate-spin"
                aria-hidden="true"
              />
              <Download
                v-else
                :size="16"
                aria-hidden="true"
              />
              <span>{{ installingSkillName === skill.name ? "Installing…" : "Install" }}</span>
            </Button>
          </div>
        </div>
      </article>
    </div>

    <div
      v-if="error && hasCatalog"
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
