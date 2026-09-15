<script setup lang="ts">
import { AlertCircle, LoaderCircle, Sparkles } from "lucide-vue-next";
import { useBuiltInSkills } from "@/composables/use-built-in-skills";

const { skills, isLoading, error, savingName, setEnabled } = useBuiltInSkills();
</script>

<template>
  <div class="space-y-4">
    <p class="text-sm text-muted">
      Skills that come with Fleet. Each one is off until you turn it on, and sessions you start afterwards get it.
      Their names start with <code class="font-mono text-xs text-text">fleet-</code>, so they never take the place of
      a skill of your own. If one overlaps with yours, leave Fleet's off.
    </p>

    <div
      v-if="isLoading"
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
        class="flex items-start justify-between gap-4 rounded-card border border-border bg-main-bg p-4"
      >
        <div class="min-w-0">
          <div class="flex items-center gap-2">
            <Sparkles
              :size="16"
              class="shrink-0 text-muted"
              aria-hidden="true"
            />
            <h3 class="truncate font-mono text-sm font-semibold text-text">
              {{ skill.name }}
            </h3>
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
      </article>
    </div>

    <div
      v-if="error"
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
