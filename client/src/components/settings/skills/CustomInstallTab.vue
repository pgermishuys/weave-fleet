<script setup lang="ts">
import { shallowRef } from "vue";
import { AlertCircle, Download, LoaderCircle } from "lucide-vue-next";
import { useSkills } from "@/composables/use-skills";
import { Button } from "@/components/ui/button";
import type { InstallTarget } from "@/lib/install-target";
import InstallTargetPicker from "../InstallTargetPicker.vue";

const target = defineModel<InstallTarget>("target", { required: true });

const { installSkill } = useSkills();

const installUrl = shallowRef("");
const isInstalling = shallowRef(false);
const formError = shallowRef<string | null>(null);

async function submitInstall(): Promise<void> {
  const url = installUrl.value.trim();
  if (!url) {
    formError.value = "Skill URL or path is required.";
    return;
  }

  isInstalling.value = true;
  formError.value = null;

  try {
    await installSkill({ url, target: target.value });
    installUrl.value = "";
  } catch (installError) {
    formError.value = installError instanceof Error ? installError.message : "Failed to install skill.";
  } finally {
    isInstalling.value = false;
  }
}
</script>

<template>
  <div class="space-y-4">
    <div class="rounded-card border border-border bg-main-bg p-4">
      <form
        class="space-y-3"
        @submit.prevent="submitInstall"
      >
        <div>
          <h3 class="text-sm font-semibold text-text">
            Install from URL or path
          </h3>
          <p class="mt-1 text-xs text-muted">
            Install a skill from a GitHub URL or local file path.
          </p>
        </div>

        <InstallTargetPicker
          v-model="target"
          kind="skills"
          :disabled="isInstalling"
        />

        <label class="grid gap-1 text-sm text-text">
          <span class="text-xs font-medium uppercase tracking-wide text-muted">Skill URL or Path</span>
          <input
            v-model="installUrl"
            type="text"
            class="w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors placeholder:text-muted focus:border-accent"
            placeholder="https://github.com/user/skill or /path/to/skill"
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

        <Button
          type="submit"
          variant="default"
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
        </Button>
      </form>
    </div>

    <div class="rounded-card border border-border bg-card-bg p-4">
      <h4 class="text-sm font-semibold text-text">
        Examples
      </h4>
      <ul class="mt-2 space-y-1 text-xs text-muted">
        <li class="font-mono">
          https://github.com/username/skill-name
        </li>
        <li class="font-mono">
          https://github.com/username/repo/tree/main/skills/my-skill
        </li>
        <li class="font-mono">
          /Users/username/skills/custom-skill
        </li>
      </ul>
    </div>
  </div>
</template>
