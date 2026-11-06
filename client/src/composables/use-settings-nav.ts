import { ref } from "vue";

/** The pages the Settings menu shows. Plugins is the overview; a plugin's own page is `plugin:<plugin id>`. */
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
  | "credentials"
  | "plugins"
  | `plugin:${string}`;

/** Ids from before the pages were grouped. They still work: each lands on the page that holds the setting now. */
export type LegacySettingsSectionId =
  | "features"
  | "workspace"
  | "storage"
  | "browser"
  | "tools"
  | "mods"
  | "system"
  | "weave"
  | "workflows"
  | `integration:${string}`;

export type SettingsSectionId = SettingsPageId | LegacySettingsSectionId;

export interface SettingsTarget {
  section: SettingsPageId;
  /** A group or tab on the page to scroll to or open (`storage`, `browser`, `tools`, `mods`, `weave`, `may-do`, `harnesses`, `machines`). */
  anchor: string | null;
}

/** Where each old id lands. `integration:<id>` (from before Plugins) lands on that plugin's page. */
export const LEGACY_SECTION_TARGETS: Readonly<Record<Exclude<LegacySettingsSectionId, `integration:${string}`>, SettingsTarget>> = {
  features: { section: "general", anchor: null },
  workspace: { section: "folders", anchor: null },
  storage: { section: "folders", anchor: "storage" },
  browser: { section: "permissions", anchor: "browser" },
  tools: { section: "skills", anchor: "tools" },
  mods: { section: "skills", anchor: "mods" },
  system: { section: "updates", anchor: null },
  // Weave is on each harness's tab. Which tab is decided by the Harnesses page: the default harness's, or the first with Weave.
  weave: { section: "harnesses", anchor: "weave" },
  // Model roles moved to the Workflows page in the rail, which Settings can't open: the Workflows switch, which says where.
  workflows: { section: "labs", anchor: "workflows" },
};

export function resolveSettingsTarget(id: SettingsSectionId, anchor: string | null = null): SettingsTarget {
  if (id.startsWith("integration:")) {
    return { section: `plugin:${id.slice("integration:".length)}`, anchor };
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
/** The harness tab Harnesses shows, remembered while Settings is open. Null until one is picked: the default harness's. */
const activeHarness = ref<string | null>(null);
/** A repository Settings → Memory should show when it opens, set by whatever sent the user there. */
const memoryRepository = ref<string | null>(null);
/** Set by whatever sent the user to Add a machine; Settings → Machines opens the form and clears it. */
const addMachineRequested = ref(false);

export function useSettingsNav() {
  function setActiveSection(section: SettingsSectionId, anchor: string | null = null): void {
    const target = resolveSettingsTarget(section, anchor);
    activeSection.value = target.section;
    activeAnchor.value = target.anchor;
    // The old Weave page has no harness of its own: let Harnesses pick the tab that has Weave.
    if (section === "weave") activeHarness.value = null;
  }

  /** Goes to Harnesses on a harness's tab, optionally at one of its groups (`weave`, `may-do`); the caller navigates to /settings. */
  function openHarness(harnessId: string, anchor: string | null = null): void {
    activeHarness.value = harnessId;
    setActiveSection("harnesses", anchor);
  }

  /** Goes to Memory with the repository selected; the caller navigates to /settings. */
  function openMemory(repository: string): void {
    memoryRepository.value = repository;
    setActiveSection("memory");
  }

  /** Goes to Machines with the Add a machine form open; the caller navigates to /settings. */
  function openAddMachine(): void {
    addMachineRequested.value = true;
    setActiveSection("machines", "add-machine");
  }

  return {
    activeSection,
    activeAnchor,
    activeHarness,
    memoryRepository,
    addMachineRequested,
    openAddMachine,
    openHarness,
    openMemory,
    setActiveSection,
  };
}
