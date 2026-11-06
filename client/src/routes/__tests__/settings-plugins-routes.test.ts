import { describe, expect, it } from "vitest";
import routeTree from "@/routeTree.gen.ts?raw";

describe("settings routes", () => {
  it("resolves /settings, /settings/plugins and /settings/plugins/$pluginId", () => {
    for (const path of ["/settings", "/settings_/plugins/", "/settings_/plugins/$pluginId"]) {
      expect(routeTree).toContain(`id: '${path}'`);
    }
    expect(routeTree).toContain("'/settings/plugins/': typeof SettingsPluginsIndexRoute");
    expect(routeTree).toContain("'/settings/plugins/$pluginId': typeof SettingsPluginsPluginIdRoute");
  });
});
