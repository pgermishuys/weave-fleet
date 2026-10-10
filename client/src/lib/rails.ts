import { computed, defineComponent, h, type Component } from "vue";
import { defineContributionPoint } from "@/lib/contributions";
import { getSidebarPanels, getSidebarViews } from "@/plugins/slots";

/** The rails Fleet itself ships; the plugins' rails are open-ended, so a rail id is any string. */
export type BuiltInRailId = "board" | "sessions" | "analytics" | "automations" | "workflows" | "settings";

// `string & {}` keeps the built-in ids in editor completions while accepting a plugin's id.
export type SidebarRail = BuiltInRailId | (string & {});

/** Where a rail's icon sits in the icon rail: the top group, the plugin group in the middle, or the bottom group. */
export type RailPlacement = "top" | "plugin" | "bottom";

export interface RailDefinition {
  id: SidebarRail;
  label: string;
  /** A rail without an icon has a panel but no button. */
  icon?: Component;
  /** The route the rail opens; a rail without one only switches the panel. */
  to?: string;
  placement: RailPlacement;
  /** What fills the context panel while this rail is active. */
  panel: Component;
  /** Within a placement, lower comes first. */
  order?: number;
  /** Hidden from the icon rail while this returns false (a feature switched off). Read inside a computed. */
  enabled?: () => boolean;
  /** Set on a rail that comes from a plugin. */
  pluginId?: string;
}

/**
 * The rails Fleet ships. A plugin's rails are not contributed here: they are derived from its sidebar items and
 * panels (see `rails`), so `rails` is the list to read.
 */
export const railPoint = defineContributionPoint<RailDefinition>({
  name: "rails",
  idOf: (rail) => rail.id,
});

const placeholderPanels = new Map<string, Component>();

/** What a plugin rail shows until the plugin supplies a panel. */
function placeholderPanel(title: string): Component {
  let panel = placeholderPanels.get(title);
  if (!panel) {
    panel = defineComponent({
      name: `${title.replace(/\s+/g, "")}Panel`,
      setup: () => () =>
        h("section", { class: "context-panel__content" }, [
          h("p", { class: "context-panel__eyebrow" }, "Plugin"),
          h("h2", { class: "context-panel__title" }, `${title} Panel`),
          h("p", { class: "context-panel__description" }, `${title} integration controls will appear here.`),
        ]),
    });
    placeholderPanels.set(title, panel);
  }
  return panel;
}

const pluginRails = computed<readonly RailDefinition[]>(() => {
  const views = getSidebarViews();
  const panels = new Map(getSidebarPanels().map((panel) => [panel.viewId, panel.component]));
  const ids = new Set<string>([...views.map((view) => view.viewId), ...panels.keys()]);
  const found: RailDefinition[] = [];

  for (const id of ids) {
    const view = views.find((candidate) => candidate.viewId === id);
    const label = view?.label ?? id;
    found.push({
      id,
      label,
      icon: view?.icon,
      to: view?.defaultPath,
      placement: view?.placement ?? "plugin",
      panel: panels.get(id) ?? placeholderPanel(label),
      order: view?.order,
      pluginId: view?.pluginId,
    });
  }

  return found;
});

/** Every rail: Fleet's own and the plugins', in display order. */
export const rails = computed<readonly RailDefinition[]>(() =>
  // Array.prototype.sort is stable, so equal orders keep core first, then the plugins in the order they registered.
  [...railPoint.items.value, ...pluginRails.value].sort((left, right) => (left.order ?? 0) - (right.order ?? 0)),
);

export function getRail(id: string): RailDefinition | undefined {
  return rails.value.find((rail) => rail.id === id);
}

export function isSidebarRail(value: string): boolean {
  return getRail(value) !== undefined;
}
