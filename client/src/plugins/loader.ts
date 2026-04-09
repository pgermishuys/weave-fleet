import { builtInPlugins } from "./builtin";
import { getPlugins, registerPlugins } from "./registry";
import type { FleetPluginManifest } from "./types";

let loaded = false;

export function loadBuiltInPlugins(): readonly FleetPluginManifest[] {
  if (!loaded) {
    registerPlugins(builtInPlugins.map((pluginModule) => pluginModule.manifest));
    loaded = true;
  }

  return getPlugins();
}
