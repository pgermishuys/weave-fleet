import { defineComponent, h } from "vue";
import { BarChart3, LayoutGrid, ListTodo, Radar, MessageSquare, Settings, Workflow, Zap } from "lucide-vue-next";
import BoardControlsPanel from "@/components/board/BoardControlsPanel.vue";
import SessionsPanel from "@/components/sessions/SessionsPanel.vue";
import SettingsNavPanel from "@/components/settings/SettingsNavPanel.vue";
import AutomationsNavPanel from "@/components/automations/AutomationsNavPanel.vue";
import WorkflowsNavPanel from "@/components/workflows/WorkflowsNavPanel.vue";
import { useBoardFeature } from "@/composables/use-board-feature";
import { useJobsFeature } from "@/composables/use-jobs-feature";
import { useMissionControlFeature } from "@/composables/use-mission-control-feature";
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

// Its library is for running workflows; with them off the page only says how to turn them on.
const WorkflowsContextPanel = defineComponent({
  name: "WorkflowsContextPanel",
  setup() {
    const { isWorkflowsEnabled } = useWorkflowsFeature();

    return () => h(isWorkflowsEnabled.value ? WorkflowsNavPanel : SessionsPanel);
  },
});

// The icon rail reads these inside a computed, after its own setup has already asked for both features
// (which starts loading the preferences), so asking again here only reads.
const boardOff = () => !useBoardFeature().isBoardFeatureEnabled.value;
const workflowsOff = () => !useWorkflowsFeature().isWorkflowsEnabled.value;
const jobsOff = () => !useJobsFeature().isJobsEnabled.value;
const missionControlOff = () => !useMissionControlFeature().isMissionControlEnabled.value;

/** The rails Fleet ships. Plugin rails (GitHub, Plugins) come from the plugins' own sidebar contributions. */
railPoint.contribute("core", [
  // Fleet (mission control) comes first and leaves the rail while it's switched off, like Board.
  { id: "fleet", label: "Fleet", icon: Radar, to: "/fleet", placement: "top", order: -10, panel: SessionsPanel, hidden: missionControlOff },
  { id: "sessions", label: "Sessions", icon: MessageSquare, to: "/", placement: "top", order: 0, panel: SessionsPanel },
  // Board leaves the rail while it's switched off. Workflows stays, dimmed; its page says how to turn it on.
  { id: "board", label: "Board", icon: LayoutGrid, to: "/board", placement: "top", order: 10, panel: BoardContextPanel, hidden: boardOff },
  { id: "workflows", label: "Workflows", icon: Workflow, to: "/workflows", placement: "top", order: 20, panel: WorkflowsContextPanel, off: workflowsOff },
  // Jobs leaves the rail while it's switched off, like Board. Its page is the whole context; the sessions list stays beside it.
  { id: "jobs", label: "Jobs", icon: ListTodo, to: "/jobs", placement: "top", order: 25, panel: SessionsPanel, hidden: jobsOff },
  { id: "automations", label: "Automations", icon: Zap, to: "/automations", placement: "top", order: 30, panel: AutomationsContextPanel },
  // Analytics is a full page; its context column keeps the sessions list.
  { id: "analytics", label: "Analytics", icon: BarChart3, to: "/analytics", placement: "secondary", order: 0, panel: SessionsPanel },
  { id: "settings", label: "Settings", icon: Settings, to: "/settings", placement: "bottom", order: 40, panel: SettingsContextPanel },
]);
