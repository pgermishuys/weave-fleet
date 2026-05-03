import { ref } from "vue";

export type SettingsSectionId =
  | "workspace"
  | "credentials"
  | "appearance"
  | "skills"
  | "plugins"
  | "system";

const activeSection = ref<SettingsSectionId>("workspace");

export function useSettingsNav() {
  function setActiveSection(section: SettingsSectionId): void {
    activeSection.value = section;
  }

  return {
    activeSection,
    setActiveSection,
  };
}
