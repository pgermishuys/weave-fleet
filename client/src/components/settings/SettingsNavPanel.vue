<script setup lang="ts">
import type { Component } from "vue";
import { computed, onMounted, shallowRef } from "vue";
import { Cable, ChevronDown, FlaskConical, FolderGit2, Lightbulb, Palette, Puzzle, RefreshCw, Server, ShieldCheck, SlidersHorizontal, Wrench, Settings2 } from "lucide-vue-next";
import { useFleetUpdatesWatch } from "@/composables/use-fleet-updates";
import type { SettingsSectionId } from "@/composables/use-settings-nav";
import { useUpdateStatus } from "@/composables/use-update-status";
import { usePluginRuntime } from "@/plugins/composable";
import { getConfigPages, isPluginEnabled } from "@/plugins/slots";
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
  /** Entries nested under this one, shown while it is unfolded. */
  children?: readonly SettingsNavItem[];
  /** A small number beside the label. */
  count?: number;
}

interface SettingsNavGroup {
  id: string;
  label: string;
  items: readonly SettingsNavItem[];
}

const props = defineProps<Props>();
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
      { id: "permissions", label: "What agents can do", icon: ShieldCheck },
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

// Plugins is one entry. It unfolds into one child per plugin that is on and has a settings page, through the plugin
// contribution point, so turning a plugin off removes its child and core never names a plugin.
const pluginRuntime = usePluginRuntime();

const pluginsItem = computed<SettingsNavItem>(() => ({
  id: "plugins",
  label: "Plugins",
  icon: Puzzle,
  count: pluginRuntime.manifests.value.filter((m) => !m.descriptor.alwaysOn && isPluginEnabled(m.descriptor.id)).length,
  children: getConfigPages().filter((page) => isPluginEnabled(page.pluginId)).map((page): SettingsNavItem => ({
    id: `plugin:${page.pluginId}`,
    label: page.navLabel ?? page.title,
    icon: page.icon ?? Settings2,
  })),
}));

const groups = computed<readonly SettingsNavGroup[]>(() => [
  ...coreGroups,
  { id: "plugins", label: "Plugins", items: [pluginsItem.value] },
]);

const inPlugins = computed(() => props.modelValue === "plugins" || props.modelValue.startsWith("plugin:"));
// Unfolded while one of its pages is open; the chevron folds it or opens it from anywhere.
const folded = shallowRef<boolean | null>(null);
const unfolded = computed(() => folded.value === null ? inPlugins.value : !folded.value);

function toggleFold(): void {
  folded.value = unfolded.value;
}

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
        <template
          v-for="item in group.items"
          :key="item.id"
        >
          <div class="settings-nav__row">
            <button
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
                class="rounded-full bg-accent px-1.5 text-[0.71429rem] font-semibold leading-4 text-white"
                :aria-label="`${updatesWaiting} ${updatesWaiting === 1 ? 'machine' : 'machines'} can update`"
                data-testid="updates-badge"
              >{{ updatesWaiting }}</span>
              <span
                v-if="item.count !== undefined"
                class="ml-auto text-[0.78571rem] text-muted"
                data-testid="settings-nav-count"
              >{{ item.count }}</span>
            </button>
            <button
              v-if="item.children"
              type="button"
              class="settings-nav__fold"
              :aria-label="unfolded ? `Fold ${item.label}` : `Unfold ${item.label}`"
              :aria-expanded="unfolded"
              @click="toggleFold"
            >
              <ChevronDown
                :size="14"
                class="settings-nav__chevron"
                :class="{ 'settings-nav__chevron--folded': !unfolded }"
                aria-hidden="true"
              />
            </button>
          </div>
          <template v-if="item.children && unfolded">
            <button
              v-for="child in item.children"
              :key="child.id"
              type="button"
              class="settings-nav__item settings-nav__item--child"
              :class="{ 'settings-nav__item--active': modelValue === child.id }"
              :aria-current="modelValue === child.id ? 'page' : undefined"
              @click="selectSection(child.id)"
            >
              <component
                :is="child.icon"
                :size="15"
                class="settings-nav__icon"
                aria-hidden="true"
              />
              <span>{{ child.label }}</span>
            </button>
          </template>
        </template>
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
  font-size: 0.71429rem;
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
  font-size: 0.71429rem;
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
  font-size: 0.85714rem;
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

.settings-nav__row {
  position: relative;
  display: flex;
}

.settings-nav__row .settings-nav__item {
  padding-right: 36px;
}

.settings-nav__fold {
  position: absolute;
  top: 0;
  right: 4px;
  bottom: 0;
  display: grid;
  place-items: center;
  width: 28px;
  border: 0;
  background: transparent;
  color: var(--muted);
}

.settings-nav__fold:hover {
  color: var(--text);
}

.settings-nav__chevron {
  transition: transform var(--transition);
}

.settings-nav__chevron--folded {
  transform: rotate(-90deg);
}

.settings-nav__item--child {
  min-height: 32px;
  margin-left: 14px;
  width: calc(100% - 14px);
}

.settings-nav__icon {
  flex-shrink: 0;
}
</style>
