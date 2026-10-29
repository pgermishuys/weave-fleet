import { ref } from "vue";
import { getConfigPages } from "@/plugins/slots";

/** The pages the Settings menu shows. An add-on's page is `integration:<plugin id>`. */
export type SettingsPageId =
  | "general"
  | "appearance"
  | "harnesses"
  | "permissions"
  | "skills"
  | "memory"
  | "folders"
  | "machines"
  | "labs"
  | "updates"
  | "weave"
  | "workflows"
  | "credentials"
  | `integration:${string}`;

/** Ids from before the pages were grouped. They still work: each lands on the page that holds the setting now. */
export type LegacySettingsSectionId =
  | "features"
  | "workspace"
  | "storage"
  | "browser"
  | "tools"
  | "mods"
  | "system"
  | "plugins";

export type SettingsSectionId = SettingsPageId | LegacySettingsSectionId;

export interface SettingsTarget {
  section: SettingsPageId;
  /** A group or tab on the page to scroll to or open (`storage`, `browser`, `tools`, `mods`). */
  anchor: string | null;
}

/** Where each old id lands. `plugins` is decided at call time: the first integration, or General. */
export const LEGACY_SECTION_TARGETS: Readonly<Record<Exclude<LegacySettingsSectionId, "plugins">, SettingsTarget>> = {
  features: { section: "general", anchor: null },
  workspace: { section: "folders", anchor: null },
  storage: { section: "folders", anchor: "storage" },
  browser: { section: "permissions", anchor: "browser" },
  tools: { section: "skills", anchor: "tools" },
  mods: { section: "skills", anchor: "mods" },
  system: { section: "updates", anchor: null },
};

export function resolveSettingsTarget(
  id: SettingsSectionId,
  anchor: string | null = null,
  firstIntegration: () => string | null = () => getConfigPages()[0]?.pluginId ?? null,
): SettingsTarget {
  if (id === "plugins") {
    const plugin = firstIntegration();
    return plugin ? { section: `integration:${plugin}`, anchor } : { section: "general", anchor };
  }
  if (id in LEGACY_SECTION_TARGETS) {
    const target = LEGACY_SECTION_TARGETS[id as keyof typeof LEGACY_SECTION_TARGETS];
    return { section: target.section, anchor: anchor ?? target.anchor };
  }
  return { section: id as SettingsPageId, anchor };
}

const activeSection = ref<SettingsPageId>("general");
/** The group or tab the page should open at, set with the section; the page clears it once it has gone there. */
const activeAnchor = ref<string | null>(null);
/** A repository Settings → Memory should show when it opens, set by whatever sent the user there. */
const memoryRepository = ref<string | null>(null);

export function useSettingsNav() {
  function setActiveSection(section: SettingsSectionId, anchor: string | null = null): void {
    const target = resolveSettingsTarget(section, anchor);
    activeSection.value = target.section;
    activeAnchor.value = target.anchor;
  }

  /** Goes to Memory with the repository selected; the caller navigates to /settings. */
  function openMemory(repository: string): void {
    memoryRepository.value = repository;
    setActiveSection("memory");
  }

  return {
    activeSection,
    activeAnchor,
    memoryRepository,
    openMemory,
    setActiveSection,
  };
}
