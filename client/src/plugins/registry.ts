import { shallowRef } from "vue";
import { defineContributionPoint } from "@/lib/contributions";
import type {
  FleetPluginBoardSource,
  FleetPluginConfigPage,
  FleetPluginManifest,
  FleetPluginRepositorySource,
  FleetPluginSettingsSection,
  FleetPluginSidebarItem,
  FleetPluginSidebarPanel,
} from "./types";

export type RegisteredSidebarItem = FleetPluginSidebarItem & { pluginId: string };
export type RegisteredSidebarPanel = FleetPluginSidebarPanel & { pluginId: string };
export type RegisteredSettingsSection = FleetPluginSettingsSection & { pluginId: string };
export type RegisteredConfigPage = FleetPluginConfigPage & { pluginId: string };

// A plugin's contributions are owned by its id. Within each point ids are unique and a later one replaces an
// earlier one (see lib/contributions.ts), so registering a plugin id again replaces that plugin whole.
export const pluginManifests = defineContributionPoint<FleetPluginManifest>({
  name: "plugins",
  idOf: (manifest) => manifest.descriptor.id,
});

export const sidebarItems = defineContributionPoint<RegisteredSidebarItem>({
  name: "plugin sidebar items",
  idOf: (item) => item.viewId,
});

export const sidebarPanels = defineContributionPoint<RegisteredSidebarPanel>({
  name: "plugin sidebar panels",
  idOf: (panel) => panel.viewId,
});

export const settingsSections = defineContributionPoint<RegisteredSettingsSection>({
  name: "plugin settings sections",
  idOf: (section) => section.id,
  labelOf: (section) => section.title,
});

export const configPages = defineContributionPoint<RegisteredConfigPage>({
  name: "plugin config pages",
  idOf: (page) => page.pluginId,
});

export const repositorySources = defineContributionPoint<FleetPluginRepositorySource>({
  name: "plugin repository sources",
  idOf: (source) => source.id,
});

export const boardSources = defineContributionPoint<FleetPluginBoardSource>({
  name: "plugin board sources",
  idOf: (source) => source.id,
});

const withPluginId = <T extends object>(pluginId: string, items: readonly T[] | undefined) =>
  (items ?? []).map((item) => ({ ...item, pluginId }));

/** Where the list of switched-off plugins is kept for everyone using this server (a preference). */
export const DISABLED_PLUGINS_PREFERENCE_KEY = "plugins.disabled";
// The server's list is the truth. This copy of it lets the first paint hide a plugin that is off before preferences
// arrive, and is what older versions kept here as the only copy.
export const DISABLED_STORAGE_KEY = "fleet.plugins.disabled";

export function parseDisabledPlugins(raw: string | null | undefined): ReadonlySet<string> {
  try {
    const parsed: unknown = JSON.parse(raw ?? "[]");
    return new Set(Array.isArray(parsed) ? parsed.filter((id): id is string => typeof id === "string") : []);
  } catch {
    return new Set();
  }
}

function loadDisabled(): ReadonlySet<string> {
  try {
    return parseDisabledPlugins(localStorage.getItem(DISABLED_STORAGE_KEY));
  } catch {
    return new Set();
  }
}

/** True when this browser has a copy of the list from an earlier visit, so the first paint can already be right. */
export function hasCachedDisabledPlugins(): boolean {
  try {
    return localStorage.getItem(DISABLED_STORAGE_KEY) !== null;
  } catch {
    return false;
  }
}

// Until the server's list has arrived and this browser has no copy, nobody knows which plugins are off: contribute
// nothing rather than flash a rail icon that then goes. Only the app holds; tests and the registry alone never do.
const held = shallowRef(false);

/** Contributes nothing (except alwaysOn plugins) until `setDisabledPlugins` is called. */
export function holdPluginsUntilKnown(): void {
  held.value = true;
}

// The switch a user flips on the Plugins page: the ids that are off, kept in this browser like other preferences.
// A plugin is on unless it is listed, so a plugin added later starts on.
const disabledIds = shallowRef<ReadonlySet<string>>(loadDisabled());

export function isPluginEnabled(pluginId: string): boolean {
  return !disabledIds.value.has(pluginId);
}

/** Turns a plugin on or off: off removes everything it contributes (rail icon, settings page, pickers) until it is on again. */
export function setPluginEnabled(pluginId: string, enabled: boolean): void {
  const next = new Set(disabledIds.value);
  if (enabled) next.delete(pluginId);
  else next.add(pluginId);
  disabledIds.value = next;
  cacheDisabled(next);

  const manifest = pluginManifests.get(pluginId);
  if (manifest) contribute(manifest);
}

function cacheDisabled(ids: ReadonlySet<string>): void {
  try {
    localStorage.setItem(DISABLED_STORAGE_KEY, JSON.stringify([...ids]));
  } catch {
    // Storage can be full or blocked; the switch still works until the page is reloaded.
  }
}

/** Takes the server's list of switched-off plugins as the truth: what each one adds comes or goes to match. */
export function setDisabledPlugins(ids: ReadonlySet<string>): void {
  const same = ids.size === disabledIds.value.size && [...ids].every((id) => disabledIds.value.has(id));
  const wasHeld = held.value;
  held.value = false;
  if (same && !wasHeld) return;
  disabledIds.value = new Set(ids);
  cacheDisabled(ids);
  for (const manifest of pluginManifests.items.value) contribute(manifest);
}

/** The ids that are switched off now. */
export function disabledPluginIds(): ReadonlySet<string> {
  return disabledIds.value;
}

/** Registers a plugin. It is listed whether or not it is on; it only contributes while it is on. */
export function registerPlugin(manifest: FleetPluginManifest): void {
  pluginManifests.contribute(manifest.descriptor.id, [manifest]);
  contribute(manifest);
}

function contribute(manifest: FleetPluginManifest): void {
  const pluginId = manifest.descriptor.id;
  const contributions = manifest.contributions;

  // Contributing again replaces the plugin, so what the old one contributed goes first.
  removePlugin(pluginId);
  if (!manifest.descriptor.alwaysOn && (held.value || !isPluginEnabled(pluginId))) return;

  sidebarItems.contribute(pluginId, withPluginId(pluginId, contributions?.sidebarItems));
  sidebarPanels.contribute(pluginId, withPluginId(pluginId, contributions?.sidebarPanels));
  settingsSections.contribute(pluginId, withPluginId(pluginId, contributions?.settingsSections));
  if (contributions?.configPage) {
    configPages.contribute(pluginId, withPluginId(pluginId, [contributions.configPage]));
  }
  repositorySources.contribute(pluginId, contributions?.repositorySources ?? []);
  boardSources.contribute(pluginId, contributions?.boardSources ?? []);
}

export function registerPlugins(manifests: readonly FleetPluginManifest[]): void {
  for (const manifest of manifests) {
    registerPlugin(manifest);
  }
}

function removePlugin(pluginId: string): void {
  for (const point of [sidebarItems, sidebarPanels, settingsSections, configPages, repositorySources, boardSources]) {
    point.removeByOwner(pluginId);
  }
}

export function clearPlugins(): void {
  for (const point of [pluginManifests, sidebarItems, sidebarPanels, settingsSections, configPages, repositorySources, boardSources]) {
    point.clear();
  }
}
