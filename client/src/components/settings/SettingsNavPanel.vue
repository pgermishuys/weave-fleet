<script setup lang="ts">
import type { Component } from "vue";
import { computed, onMounted } from "vue";
import { Cable, FlaskConical, FolderGit2, Lightbulb, Palette, RefreshCw, Server, ShieldCheck, SlidersHorizontal, Wrench, Settings2 } from "lucide-vue-next";
import { useFleetUpdatesWatch } from "@/composables/use-fleet-updates";
import type { SettingsSectionId } from "@/composables/use-settings-nav";
import { useUpdateStatus } from "@/composables/use-update-status";
import { getConfigPages } from "@/plugins/slots";
import { useModsStore } from "@/stores/mods";

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

interface SettingsNavGroup {
  id: string;
  label: string;
  items: readonly SettingsNavItem[];
}

defineProps<Props>();
const emit = defineEmits<Emits>();

const { isUpdateAvailable, isUpdateStaged } = useUpdateStatus();

const showUpdateDot = computed(() => isUpdateAvailable.value || isUpdateStaged.value);

// With the Updates switch (Labs) on, the Updates & about entry carries a badge: how many machines can update.
const { waiting: updatesWaiting } = useFleetUpdatesWatch();

// The switch decides whether Skills & tools has a Mods tab; read it once here so that is right on first paint.
const mods = useModsStore();
onMounted(() => { void mods.loadSwitch(); });

const coreGroups: readonly SettingsNavGroup[] = [
  {
    id: "you",
    label: "You",
    items: [
      { id: "general", label: "General", icon: SlidersHorizontal },
      { id: "appearance", label: "Appearance", icon: Palette },
    ],
  },
  {
    id: "agents",
    label: "Agents",
    items: [
      { id: "harnesses", label: "Harnesses", icon: Cable },
      { id: "permissions", label: "Permissions", icon: ShieldCheck },
      { id: "skills", label: "Skills & tools", icon: Wrench },
      { id: "memory", label: "Memory", icon: Lightbulb },
    ],
  },
  {
    id: "work",
    label: "Work",
    items: [
      { id: "folders", label: "Folders", icon: FolderGit2 },
      { id: "machines", label: "Machines", icon: Server },
    ],
  },
  {
    id: "fleet",
    label: "Fleet",
    items: [
      { id: "labs", label: "Labs", icon: FlaskConical },
      { id: "updates", label: "Updates & about", icon: RefreshCw },
    ],
  },
];

// One entry per add-on that offers a settings page, through the plugin contribution point.
const groups = computed<readonly SettingsNavGroup[]>(() => {
  const integrations = getConfigPages().map((page): SettingsNavItem => ({
    id: `integration:${page.pluginId}`,
    label: page.navLabel ?? page.title,
    icon: page.icon ?? Settings2,
  }));
  return integrations.length > 0
    ? [...coreGroups, { id: "integrations", label: "Integrations", items: integrations }]
    : coreGroups;
});

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
      <div
        v-for="group in groups"
        :key="group.id"
        class="settings-nav__group"
        role="group"
        :aria-label="group.label"
        :data-settings-nav-group="group.id"
      >
        <p class="settings-nav__group-label">
          {{ group.label }}
        </p>
        <button
          v-for="item in group.items"
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
          <!-- Update notification dot for Updates & about -->
          <span
            v-if="item.id === 'updates' && showUpdateDot"
            class="ml-auto h-2 w-2 shrink-0 rounded-full"
            :class="isUpdateStaged ? 'bg-warn' : 'bg-accent'"
            aria-label="Update available"
          />
          <span
            v-if="item.id === 'updates' && updatesWaiting"
            :class="showUpdateDot ? 'ml-1.5' : 'ml-auto'"
            class="rounded-full bg-accent px-1.5 text-[10px] font-semibold leading-4 text-white"
            :aria-label="`${updatesWaiting} ${updatesWaiting === 1 ? 'machine' : 'machines'} can update`"
            data-testid="updates-badge"
          >{{ updatesWaiting }}</span>
        </button>
      </div>
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
  gap: 0;
  padding: 0 12px 12px;
}

.settings-nav__group {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin-bottom: 8px;
}

.settings-nav__group-label {
  margin: 8px 0 2px;
  padding: 0 12px;
  font-size: 10px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--muted);
  opacity: 0.8;
}

.settings-nav__item {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  min-height: 36px;
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
