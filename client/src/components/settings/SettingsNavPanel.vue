<script setup lang="ts">
import type { Component } from "vue";
import { computed, onMounted } from "vue";
import { Blocks, Cable, FolderGit2, Globe, Info, Lightbulb, Palette, Puzzle, Server, ShieldCheck, SlidersHorizontal, Waves, Workflow, Wrench } from "lucide-vue-next";
import { useUpdateStatus } from "@/composables/use-update-status";
import { useModsStore } from "@/stores/mods";

type SettingsSectionId =
  | "workspace"
  | "credentials"
  | "appearance"
  | "skills"
  | "memory"
  | "mods"
  | "browser"
  | "permissions"
  | "tools"
  | "features"
  | "harnesses"
  | "machines"
  | "weave"
  | "workflows"
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

const mods = useModsStore();
// The switch decides whether Mods has a place in the nav; read it once here so the entry is right on first paint.
onMounted(() => { void mods.loadSwitch(); });

const allItems: readonly SettingsNavItem[] = [
  { id: "workspace", label: "Folders", icon: FolderGit2 },
  { id: "appearance", label: "Appearance", icon: Palette },
  { id: "features", label: "Features", icon: SlidersHorizontal },
  { id: "skills", label: "Skills", icon: Wrench },
  { id: "memory", label: "Memory", icon: Lightbulb },
  { id: "mods", label: "Mods", icon: Blocks },
  { id: "permissions", label: "Permissions", icon: ShieldCheck },
  { id: "browser", label: "Browser", icon: Globe },
  { id: "tools", label: "Tools", icon: Puzzle },
  { id: "harnesses", label: "Harnesses", icon: Cable },
  { id: "machines", label: "Machines", icon: Server },
  { id: "weave", label: "Weave", icon: Waves },
  { id: "workflows", label: "Workflows", icon: Workflow },
  { id: "system", label: "System", icon: Info },
];

const items = computed(() => allItems.filter((item) => item.id !== "mods" || mods.isSwitchedOn));

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
  background: transparent;
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
  transition: background-color var(--transition), border-color var(--transition), color var(--transition);
}

.settings-nav__item:hover {
  background: var(--bg);
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
