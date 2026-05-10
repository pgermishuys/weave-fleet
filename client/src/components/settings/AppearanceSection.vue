<script setup lang="ts">
import { computed } from "vue";
import { Check, Monitor } from "lucide-vue-next";
import { useThemeStore, themes, type ThemeSelection } from "@/stores/theme";

const themeStore = useThemeStore();

const activeTheme = computed(() => themeStore.currentTheme);

function selectTheme(theme: ThemeSelection): void {
  themeStore.setTheme(theme);
}
</script>

<template>
  <section class="rounded-card border border-border bg-card-bg p-6 shadow-sm">
    <div class="flex flex-col gap-1">
      <h2 class="text-lg font-semibold text-text">
        Appearance
      </h2>
      <p class="text-sm text-muted">
        Choose how Weave should look across the workspace shell.
      </p>
    </div>

    <div class="mt-5 grid gap-3 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4">
      <!-- System option -->
      <button
        type="button"
        class="flex flex-col gap-3 rounded-card border p-3 text-left transition-colors"
        :class="activeTheme === 'system'
          ? 'border-accent bg-accent-dim'
          : 'border-border hover:border-accent/50'"
        @click="selectTheme('system')"
      >
        <div class="flex h-10 items-center justify-center rounded-btn border border-border/60 bg-gradient-to-r from-[#0a0a0b] via-[#0a0a0b] to-[#f1f5f9]">
          <Monitor
            :size="14"
            class="text-white/70"
          />
        </div>

        <div class="flex items-center justify-between gap-2">
          <span class="text-xs font-medium text-text">System</span>
          <Check
            v-if="activeTheme === 'system'"
            :size="14"
            class="text-accent"
          />
        </div>
      </button>

      <!-- Theme options -->
      <button
        v-for="theme in themes"
        :key="theme.id"
        type="button"
        class="flex flex-col gap-3 rounded-card border p-3 text-left transition-colors"
        :class="activeTheme === theme.id
          ? 'border-accent bg-accent-dim'
          : 'border-border hover:border-accent/50'"
        @click="selectTheme(theme.id)"
      >
        <!-- Mini preview swatch -->
        <div
          class="flex h-10 items-end gap-0.5 overflow-hidden rounded-btn border border-border/60 p-1.5"
          :style="{ backgroundColor: theme.swatches[0] }"
        >
          <div
            class="h-full flex-1 rounded-sm"
            :style="{ backgroundColor: theme.swatches[1] }"
          />
          <div
            class="h-3/4 w-2 rounded-sm"
            :style="{ backgroundColor: theme.swatches[2] }"
          />
          <div
            class="h-1/2 w-2 rounded-sm"
            :style="{ backgroundColor: theme.swatches[3], opacity: 0.6 }"
          />
        </div>

        <div class="flex items-center justify-between gap-2">
          <span class="text-xs font-medium text-text">{{ theme.label }}</span>
          <Check
            v-if="activeTheme === theme.id"
            :size="14"
            class="text-accent"
          />
        </div>
      </button>
    </div>

    <p class="mt-4 text-xs text-muted">
      Active: <span class="font-medium text-text">{{ themeStore.resolvedTheme.label }}</span>
    </p>
  </section>
</template>
