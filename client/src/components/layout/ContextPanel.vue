<script setup lang="ts">
import type { Component } from "vue";
import type { SidebarRail } from "@/stores/sidebar";
import { computed, defineComponent, h, shallowRef } from "vue";
import { storeToRefs } from "pinia";
import BoardControlsPanel from "@/components/board/BoardControlsPanel.vue";
import SessionsPanel from "@/components/sessions/SessionsPanel.vue";
import SettingsNavPanel from "@/components/settings/SettingsNavPanel.vue";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { getSidebarPanels } from "@/plugins/slots";
import { useSidebarStore } from "@/stores/sidebar";

type PluginRailId =
  | "github"
  | "marketplace"
  | "settings";

type ContextPanelKey = SidebarRail | PluginRailId;

interface PluginPanelDefinition {
  id: PluginRailId;
  title: string;
}

const SettingsContextPanel = defineComponent({
  name: "SettingsContextPanel",
  setup() {
    const { activeSection, setActiveSection } = useSettingsNav();

    return () =>
      h(SettingsNavPanel, {
        modelValue: activeSection.value,
        "onUpdate:modelValue": setActiveSection,
      });
  },
});

function createPlaceholderPanel(
  eyebrow: string,
  title: string,
  description: string,
): Component {
  return defineComponent({
    name: `${title.replace(/\s+/g, "")}Panel`,
    setup() {
      return () =>
        h("section", { class: "context-panel__content" }, [
          h("p", { class: "context-panel__eyebrow" }, eyebrow),
          h("h2", { class: "context-panel__title" }, title),
          h("p", { class: "context-panel__description" }, description),
        ]);
    },
  });
}

const pluginPanelRegistry = [
  { id: "github", title: "GitHub" },
  { id: "marketplace", title: "Plugins" },
  { id: "settings", title: "Settings" },
] as const satisfies readonly PluginPanelDefinition[];

const pluginPanels = Object.fromEntries(
  pluginPanelRegistry.map(({ id, title }) => [
    id,
    id === "settings"
      ? SettingsContextPanel
      : createPlaceholderPanel(
          "Plugin",
          `${title} Panel`,
          `${title} integration controls will appear here.`,
        ),
  ]),
) as Record<PluginRailId, Component>;

const registeredPluginPanels = computed<Record<PluginRailId, Component>>(() => {
  const registeredEntries = getSidebarPanels()
    .filter((panel): panel is typeof panel & { viewId: PluginRailId } => panel.viewId in pluginPanels)
    .map((panel) => [panel.viewId, panel.component] as const);

  return {
    ...pluginPanels,
    ...Object.fromEntries(registeredEntries),
  };
});

const panelComponents = computed<Record<ContextPanelKey, Component>>(() => ({
  sessions: SessionsPanel,
  board: BoardControlsPanel,
  analytics: SessionsPanel,
  ...registeredPluginPanels.value,
}));

function isContextPanelKey(value: string, panels: Record<ContextPanelKey, Component>): value is ContextPanelKey {
  return value in panels;
}

const sidebarStore = useSidebarStore();
const { activeRail } = storeToRefs(sidebarStore);

const activePanel = computed<Component>(() => {
  const rail = activeRail.value as string;

  if (isContextPanelKey(rail, panelComponents.value)) {
    return panelComponents.value[rail];
  }

  return SessionsPanel;
});

const activePanelKey = computed(() => {
  const rail = activeRail.value as string;

  return isContextPanelKey(rail, panelComponents.value) ? rail : "sessions";
});

const MIN_WIDTH = 200;
const MAX_WIDTH = 500;
const panelWidth = shallowRef(280);
const isResizing = shallowRef(false);

function onResizeStart(event: PointerEvent): void {
  const target = event.currentTarget as HTMLElement;
  target.setPointerCapture(event.pointerId);
  isResizing.value = true;
  const startX = event.clientX;
  const startWidth = panelWidth.value;

  function onMove(e: PointerEvent): void {
    const delta = e.clientX - startX;
    panelWidth.value = Math.min(MAX_WIDTH, Math.max(MIN_WIDTH, startWidth + delta));
  }

  function onUp(): void {
    isResizing.value = false;
    target.removeEventListener("pointermove", onMove);
    target.removeEventListener("pointerup", onUp);
  }

  target.addEventListener("pointermove", onMove);
  target.addEventListener("pointerup", onUp);
}
</script>

<template>
  <aside
    class="context-panel"
    :style="{ width: `${panelWidth}px`, minWidth: `${panelWidth}px` }"
    aria-label="Context panel"
  >
    <component
      :is="activePanel"
      :key="activePanelKey"
    />
    <div
      class="resize-handle"
      :class="{ 'resize-handle--active': isResizing }"
      @pointerdown.prevent="onResizeStart"
    />
  </aside>
</template>

<style scoped>
.context-panel {
  position: relative;
  background: var(--panel-bg);
  border-right: 1px solid var(--border);
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.resize-handle {
  position: absolute;
  top: 0;
  right: -3px;
  width: 6px;
  height: 100%;
  cursor: col-resize;
  z-index: 20;
}

.resize-handle:hover,
.resize-handle--active {
  background: var(--accent);
  opacity: 0.4;
}

.context-panel :deep(.context-panel__content) {
  display: flex;
  flex: 1;
  flex-direction: column;
  justify-content: center;
  gap: 8px;
  min-height: 0;
  padding: 24px;
}

.context-panel :deep(.context-panel__eyebrow) {
  margin: 0;
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  color: var(--muted);
}

.context-panel :deep(.context-panel__title) {
  margin: 0;
  font-size: 18px;
  font-weight: 600;
  color: var(--text);
}

.context-panel :deep(.context-panel__description) {
  margin: 0;
  font-size: 13px;
  line-height: 1.5;
  color: var(--muted);
}
</style>
