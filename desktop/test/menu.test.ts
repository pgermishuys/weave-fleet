import { describe, expect, it, vi } from "vitest";

const built: { template: unknown[] }[] = [];
vi.mock("electron", () => ({
  Menu: { buildFromTemplate: (template: unknown[]) => { built.push({ template }); return {}; } },
  clipboard: {},
  shell: {},
}));

import { buildAppMenu } from "../src/menu";

describe("application menu", () => {
  it("has Edit → Find in Conversation, shown with Ctrl/⌘F but with the key left to the page", () => {
    const findInConversation = vi.fn();
    for (const platform of ["linux", "darwin"] as const) {
      buildAppMenu(platform, { openLogs: vi.fn(), openDataFolder: vi.fn(), checkForUpdates: vi.fn(), reportProblem: vi.fn(), findInConversation });
      const edit = (built.at(-1)!.template as { label?: string; submenu?: { label?: string; accelerator?: string; registerAccelerator?: boolean; click?: () => void }[] }[])
        .find((entry) => entry.label === "Edit");
      const item = edit?.submenu?.find((entry) => entry.label === "Find in Conversation");
      expect(item?.accelerator).toBe("CommandOrControl+F");
      expect(item?.registerAccelerator).toBe(false);
      item?.click?.();
    }
    expect(findInConversation).toHaveBeenCalledTimes(2);
  });

  it("on Windows, where the accelerator can't be left to the page, shows the key as text instead of registering it", () => {
    const findInConversation = vi.fn();
    buildAppMenu("win32", { openLogs: vi.fn(), openDataFolder: vi.fn(), checkForUpdates: vi.fn(), reportProblem: vi.fn(), findInConversation });
    const edit = (built.at(-1)!.template as { label?: string; submenu?: { label?: string; accelerator?: string }[] }[]).find((entry) => entry.label === "Edit");
    const item = edit?.submenu?.find((entry) => entry.label?.startsWith("Find in Conversation"));
    expect(item?.label).toBe("Find in Conversation\tCtrl+F");
    expect(item?.accelerator).toBeUndefined();
  });
});
