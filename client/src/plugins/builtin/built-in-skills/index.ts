import { Sparkles } from "lucide-vue-next";
import BuiltInSkillsTab from "@/components/settings/skills/BuiltInSkillsTab.vue";
import type { FleetPluginManifest } from "@/plugins/types";

export const builtInSkillsPluginManifest = {
  descriptor: {
    id: "built-in-skills",
    displayName: "Built-in skills",
    trustLevel: "built-in",
    hasFrontend: true,
    hasBackend: true,
    description: "Skills that come with Fleet: review, simplify, run, mockups and more. Each has its own switch.",
    // Fleet has no master switch for them: each skill is off until turned on, on its page here.
    switchless: true,
  },
  contributions: {
    configPage: {
      title: "Built-in skills",
      component: BuiltInSkillsTab,
      icon: Sparkles,
    },
  },
} as const satisfies FleetPluginManifest;

export default builtInSkillsPluginManifest;
