<script setup lang="ts">
import type { Component } from "vue";
import { computed } from "vue";
import { FolderGit2, Info, Cpu, Palette, SlidersHorizontal, Wrench } from "lucide-vue-next";
import { useUpdateStatus } from "@/composables/use-update-status";

type SettingsSectionId =
  | "workspace"
  | "credentials"
  | "appearance"
  | "skills"
  | "features"
  | "nucode"
  | "plugins"
  | "system";

interface Props {
  modelValue: string;
}

interface Emits {
  "update:modelValue": [value: SettingsSectionId];
}

interface SettingsNavItem {
  id: SettingsSectionId;
  label: string;
  icon: Component;
}

defineProps<Props>();
const emit = defineEmits<Emits>();

const { isUpdateAvailable, isUpdateStaged } = useUpdateStatus();

const showUpdateDot = computed(() => isUpdateAvailable.value || isUpdateStaged.value);

const items: readonly SettingsNavItem[] = [
  { id: "workspace", label: "Workspace", icon: FolderGit2 },
  { id: "appearance", label: "Appearance", icon: Palette },
  { id: "features", label: "Features", icon: SlidersHorizontal },
  { id: "skills", label: "Skills", icon: Wrench },
  { id: "nucode", label: "NuCode", icon: Cpu },
  { id: "system", label: "System", icon: Info },
];

function selectSection(sectionId: SettingsSectionId): void {
  emit("update:modelValue", sectionId);
}
</script>

<template>
  <section
    class="settings-nav-panel"
    aria-label="Settings navigation"
  >
    <div class="panel-header-row">
      <p class="panel-header">
        Settings
      </p>
    </div>

    <nav
      class="settings-nav"
      aria-label="Settings sections"
    >
      <button
        v-for="item in items"
        :key="item.id"
        type="button"
        class="settings-nav__item"
        :class="{ 'settings-nav__item--active': modelValue === item.id }"
        :aria-current="modelValue === item.id ? 'page' : undefined"
        @click="selectSection(item.id)"
      >
        <component
          :is="item.icon"
          :size="16"
          class="settings-nav__icon"
          aria-hidden="true"
        />
        <span>{{ item.label }}</span>
        <!-- Update notification dot for the System nav item -->
        <span
          v-if="item.id === 'system' && showUpdateDot"
          class="ml-auto h-2 w-2 rounded-full"
          :class="isUpdateStaged ? 'bg-warn' : 'bg-accent'"
          aria-label="Update available"
        />
      </button>
    </nav>
  </section>
</template>

<style scoped>
.settings-nav-panel {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  background: var(--panel-bg);
}

.panel-header-row {
  padding-top: 4px;
}

.panel-header {
  margin: 0;
  padding: 14px 16px 10px;
  font-size: 10px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--muted);
}

.settings-nav {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 4px;
  padding: 0 12px 12px;
}

.settings-nav__item {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  min-height: 40px;
  padding: 0 12px;
  border: 1px solid transparent;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  font-size: 12px;
  font-weight: 500;
  text-align: left;
  transition: background-color 0.15s ease, border-color 0.15s ease, color 0.15s ease;
}

.settings-nav__item:hover {
  background: rgba(255, 255, 255, 0.04);
  color: var(--text);
}

.settings-nav__item:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.settings-nav__item--active {
  border-color: color-mix(in srgb, var(--accent) 40%, transparent);
  background: color-mix(in srgb, var(--panel-bg) 88%, var(--accent) 12%);
  color: var(--text);
}

.settings-nav__icon {
  flex-shrink: 0;
}
</style>
