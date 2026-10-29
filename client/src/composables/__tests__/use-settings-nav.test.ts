import { beforeEach, describe, expect, it } from "vitest";
import { resolveSettingsTarget, useSettingsNav } from "@/composables/use-settings-nav";

describe("old Settings ids", () => {
  it.each([
    ["features", "general", null],
    ["workspace", "folders", null],
    ["storage", "folders", "storage"],
    ["browser", "permissions", "browser"],
    ["tools", "skills", "tools"],
    ["mods", "skills", "mods"],
    ["system", "updates", null],
    ["weave", "harnesses", "weave"],
    ["workflows", "labs", "workflows"],
  ] as const)("%s lands on %s (%s)", (old, section, anchor) => {
    expect(resolveSettingsTarget(old)).toEqual({ section, anchor });
  });

  it("sends plugins to the first integration, or General when there is none", () => {
    expect(resolveSettingsTarget("plugins", null, () => "github")).toEqual({ section: "integration:github", anchor: null });
    expect(resolveSettingsTarget("plugins", null, () => null)).toEqual({ section: "general", anchor: null });
  });

  it("leaves current ids alone", () => {
    expect(resolveSettingsTarget("labs")).toEqual({ section: "labs", anchor: null });
    expect(resolveSettingsTarget("harnesses")).toEqual({ section: "harnesses", anchor: null });
    expect(resolveSettingsTarget("skills", "mods")).toEqual({ section: "skills", anchor: "mods" });
  });
});

describe("useSettingsNav", () => {
  beforeEach(() => useSettingsNav().setActiveSection("general"));

  it("stores the new page and anchor when given an old id", () => {
    const nav = useSettingsNav();
    nav.setActiveSection("storage");
    expect(nav.activeSection.value).toBe("folders");
    expect(nav.activeAnchor.value).toBe("storage");
    nav.setActiveSection("memory");
    expect(nav.activeAnchor.value).toBeNull();
  });

  it("opens Harnesses on a harness's tab, at a group", () => {
    const nav = useSettingsNav();
    nav.openHarness("opencode2", "may-do");
    expect(nav.activeSection.value).toBe("harnesses");
    expect(nav.activeHarness.value).toBe("opencode2");
    expect(nav.activeAnchor.value).toBe("may-do");
  });

  it("sends the old Weave id to Harnesses at its Weave group, leaving the tab to be chosen", () => {
    const nav = useSettingsNav();
    nav.openHarness("pi");
    nav.setActiveSection("weave");
    expect(nav.activeSection.value).toBe("harnesses");
    expect(nav.activeAnchor.value).toBe("weave");
    expect(nav.activeHarness.value).toBeNull();
  });

  it("opens Memory with the repository", () => {
    const nav = useSettingsNav();
    nav.openMemory("/r/demo");
    expect(nav.activeSection.value).toBe("memory");
    expect(nav.memoryRepository.value).toBe("/r/demo");
  });
});
