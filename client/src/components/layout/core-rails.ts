import { defineComponent, h } from "vue";
import { BarChart3, LayoutGrid, MessageSquare, Settings, Workflow, Zap } from "lucide-vue-next";
import BoardControlsPanel from "@/components/board/BoardControlsPanel.vue";
import SessionsPanel from "@/components/sessions/SessionsPanel.vue";
import SettingsNavPanel from "@/components/settings/SettingsNavPanel.vue";
import AutomationsNavPanel from "@/components/automations/AutomationsNavPanel.vue";
import WorkflowsNavPanel from "@/components/workflows/WorkflowsNavPanel.vue";
import { useBoardFeature } from "@/composables/use-board-feature";
import { useWorkflowsFeature } from "@/composables/use-workflows-feature";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { useAutomationsNav } from "@/composables/use-automations-nav";
import { railPoint } from "@/lib/rails";

const SettingsContextPanel = defineComponent({
  name: "SettingsContextPanel",
  setup() {
    const { activeSection, setActiveSection } = useSettingsNav();

    return () =>
      h(SettingsNavPanel, {
        modelValue: activeSection.value,
        "onUpdate:modelValue": setActiveSection,
      });
  },
});

const AutomationsContextPanel = defineComponent({
  name: "AutomationsContextPanel",
  setup() {
    const { activeAutomationId, setActiveAutomation, startCreate } = useAutomationsNav();

    return () =>
      h(AutomationsNavPanel, {
        modelValue: activeAutomationId.value,
        "onUpdate:modelValue": setActiveAutomation,
        onCreate: startCreate,
      });
  },
});

// Its filters are for the board itself; with Board off the page only says how to turn it on.
const BoardContextPanel = defineComponent({
  name: "BoardContextPanel",
  setup() {
    const { isBoardFeatureEnabled } = useBoardFeature();

    return () => h(isBoardFeatureEnabled.value ? BoardControlsPanel : SessionsPanel);
  },
});

// The icon rail reads these inside a computed, after its own setup has already asked for both features
// (which starts loading the preferences), so asking again here only reads.
const boardEnabled = () => useBoardFeature().isBoardFeatureEnabled.value;
const workflowsEnabled = () => useWorkflowsFeature().isWorkflowsEnabled.value;

/** The rails Fleet ships. Plugin rails (GitHub, Plugins) come from the plugins' own sidebar contributions. */
railPoint.contribute("core", [
  { id: "board", label: "Board", icon: LayoutGrid, to: "/board", placement: "top", order: 0, panel: BoardContextPanel, enabled: boardEnabled },
  { id: "sessions", label: "Sessions", icon: MessageSquare, to: "/", placement: "top", order: 10, panel: SessionsPanel },
  { id: "workflows", label: "Workflows", icon: Workflow, to: "/workflows", placement: "bottom", order: 10, panel: WorkflowsNavPanel, enabled: workflowsEnabled },
  { id: "automations", label: "Automations", icon: Zap, to: "/automations", placement: "bottom", order: 20, panel: AutomationsContextPanel },
  // Analytics is a full page; its context column keeps the sessions list.
  { id: "analytics", label: "Analytics", icon: BarChart3, to: "/analytics", placement: "bottom", order: 30, panel: SessionsPanel },
  { id: "settings", label: "Settings", icon: Settings, to: "/settings", placement: "bottom", order: 40, panel: SettingsContextPanel },
]);
