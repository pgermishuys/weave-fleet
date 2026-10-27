import { ref } from "vue";

export type SettingsSectionId =
  | "workspace"
  | "storage"
  | "credentials"
  | "appearance"
  | "skills"
  | "memory"
  | "mods"
  | "browser"
  | "permissions"
  | "tools"
  | "features"
  | "harnesses"
  | "machines"
  | "weave"
  | "workflows"
  | "plugins"
  | "system";

const activeSection = ref<SettingsSectionId>("workspace");
/** A repository Settings → Memory should show when it opens, set by whatever sent the user there. */
const memoryRepository = ref<string | null>(null);

export function useSettingsNav() {
  function setActiveSection(section: SettingsSectionId): void {
    activeSection.value = section;
  }

  /** Goes to Memory with the repository selected; the caller navigates to /settings. */
  function openMemory(repository: string): void {
    memoryRepository.value = repository;
    activeSection.value = "memory";
  }

  return {
    activeSection,
    memoryRepository,
    openMemory,
    setActiveSection,
  };
}
