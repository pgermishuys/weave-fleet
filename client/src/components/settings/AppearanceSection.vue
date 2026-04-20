<script setup lang="ts">
import { computed } from "vue";
import { Check, Monitor, Moon, Sun } from "lucide-vue-next";
import { useThemeStore, type ThemeMode } from "@/stores/theme";

interface ThemeOption {
  value: ThemeMode;
  label: string;
  description: string;
  icon: typeof Sun;
}

const themeStore = useThemeStore();

const themeOptions: readonly ThemeOption[] = [
  {
    value: "light",
    label: "Light",
    description: "Bright surfaces for daytime work.",
    icon: Sun,
  },
  {
    value: "dark",
    label: "Dark",
    description: "Low-glare colors for focused sessions.",
    icon: Moon,
  },
  {
    value: "system",
    label: "System",
    description: "Match your operating system preference.",
    icon: Monitor,
  },
] as const;

const activeTheme = computed(() => themeStore.currentTheme);
const resolvedTheme = computed(() => themeStore.resolvedTheme);

function selectTheme(theme: ThemeMode): void {
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

    <div class="mt-5 grid gap-3 md:grid-cols-3">
      <button
        v-for="option in themeOptions"
        :key="option.value"
        type="button"
        class="flex flex-col gap-3 rounded-card border p-4 text-left transition-colors"
        :class="activeTheme === option.value
          ? 'border-accent bg-accent-dim'
          : 'border-border hover:border-accent/50'"
        @click="selectTheme(option.value)"
      >
        <div class="flex items-start justify-between gap-3">
          <div class="rounded-btn border border-border bg-main-bg p-2 text-text">
            <component
              :is="option.icon"
              :size="16"
              aria-hidden="true"
            />
          </div>
          <Check
            v-if="activeTheme === option.value"
            :size="16"
            class="text-accent"
            aria-hidden="true"
          />
        </div>

        <div class="space-y-1">
          <p class="text-sm font-semibold text-text">
            {{ option.label }}
          </p>
          <p class="text-xs text-muted">
            {{ option.description }}
          </p>
        </div>
      </button>
    </div>

    <p class="mt-4 text-xs text-muted">
      Active palette: <span class="font-medium text-text">{{ resolvedTheme }}</span>
    </p>
  </section>
</template>
