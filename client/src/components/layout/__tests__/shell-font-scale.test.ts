import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

// The font-size preference sets the root font size, so shell text has to be in rem to follow it.
const SHELL_FILES = [
  "layout/IconRail.vue",
  "layout/RailButton.vue",
  "layout/StatusBar.vue",
  "layout/ContextPanel.vue",
  "layout/CenterContent.vue",
  "layout/RightPanelTabs.vue",
  "layout/CollapsedRightRail.vue",
  "settings/SettingsNavPanel.vue",
  "automations/AutomationsNavPanel.vue",
  "workflows/WorkflowsNavPanel.vue",
  "sessions/SessionsPanel.vue",
  "sessions/SessionItem.vue",
  "sessions/ProjectGroup.vue",
  "sessions/MachineHeader.vue",
];

describe("app shell font sizes", () => {
  it.each(SHELL_FILES)("%s has no fixed px font sizes", (file) => {
    const source = readFileSync(resolve(__dirname, "../..", file), "utf8");
    expect(source.match(/font-size:\s*[0-9.]+px|text-\[[0-9.]+px\]/g) ?? []).toEqual([]);
  });
});
