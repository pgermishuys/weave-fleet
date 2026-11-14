<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { Brain, Download } from "lucide-vue-next";
import { useSkillCatalog } from "@/composables/use-skill-catalog";
import { useSkills } from "@/composables/use-skills";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import type { InstallSkillRequest } from "@/composables/use-skill-catalog";
import { isSameTarget, targetBody, type InstallTarget } from "@/lib/install-target";
import InstallTargetPicker from "../InstallTargetPicker.vue";
import { EmptyState } from "@/components/ui/empty-state";
import { ErrorNote } from "@/components/ui/error-note";
import { Spinner } from "@/components/ui/spinner";

function mapSourceToNumber(source: "GitHub" | "Local" | "Bundled"): number {
  switch (source) {
    case "Bundled":
      return 0;
    case "GitHub":
      return 1;
    case "Local":
      return 2;
  }
}

const target = defineModel<InstallTarget>("target", { required: true });

const { catalog, isLoading, error, installSkill } = useSkillCatalog();
const { skills: installedSkills, fetchSkills } = useSkills();

const installingSkillName = shallowRef<string | null>(null);
const installError = shallowRef<string | null>(null);

const hasCatalog = computed(() => catalog.value.length > 0);

/** Installed where the picker points: a skill can also be installed elsewhere. */
function isInstalled(skillName: string): boolean {
  return installedSkills.value.some((s) => s.name === skillName && isSameTarget(s.target, target.value));
}

async function handleInstall(skillName: string): Promise<void> {
  const catalogEntry = catalog.value.find((s) => s.name === skillName);
  if (!catalogEntry) {
    return;
  }

  installingSkillName.value = skillName;
  installError.value = null;

  try {
    const request: InstallSkillRequest = {
      name: catalogEntry.name,
      source: mapSourceToNumber(catalogEntry.source),
      repoUrl: catalogEntry.repoUrl ?? null,
      ref: catalogEntry.ref ?? null,
      subPath: catalogEntry.subPath ?? catalogEntry.localPath ?? null,
      localPath: catalogEntry.localPath ?? null,
      targetHarnesses: [...catalogEntry.targetHarnesses],
      ...targetBody(target.value),
    };

    await installSkill(request);
    await fetchSkills();
  } catch (err) {
    installError.value = err instanceof Error ? err.message : "Failed to install skill.";
  } finally {
    installingSkillName.value = null;
  }
}
</script>

<template>
  <div class="space-y-4">
    <InstallTargetPicker
      v-model="target"
      kind="skills"
      :disabled="installingSkillName !== null"
    />

    <div
      v-if="isLoading"
      class="flex items-center gap-2 text-sm text-muted"
    >
      <Spinner />
      <span>Loading catalog…</span>
    </div>

    <ErrorNote
      v-else-if="error && !hasCatalog"
      :message="error"
    />

    <EmptyState
      v-else-if="!hasCatalog"
      :icon="Brain"
      title="No skills in catalog"
      description="The skill catalog is empty or unavailable."
      bordered
    />

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
              <Spinner v-if="installingSkillName === skill.name" />
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

    <ErrorNote
      v-if="installError"
      :message="installError"
    />

    <ErrorNote
      v-else-if="error && hasCatalog"
      :message="error"
    />
  </div>
</template>
