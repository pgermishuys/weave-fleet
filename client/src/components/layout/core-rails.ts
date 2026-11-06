import { defineComponent, h } from "vue";
import { BarChart3, LayoutGrid, ListTodo, Radar, MessageSquare, Settings, Workflow, Zap } from "lucide-vue-next";
import FleetNavPanel from "@/components/mission-control/FleetNavPanel.vue";
import AnalyticsNavPanel from "@/components/analytics/AnalyticsNavPanel.vue";
import BoardControlsPanel from "@/components/board/BoardControlsPanel.vue";
import JobsNavPanel from "@/components/jobs/JobsNavPanel.vue";
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

// The icon rail reads these inside a computed, after its own setup has already asked for both features
// (which starts loading the preferences), so asking again here only reads.
const boardOff = () => !useBoardFeature().isBoardFeatureEnabled.value;
const workflowsOff = () => !useWorkflowsFeature().isWorkflowsEnabled.value;
const jobsOff = () => !useJobsFeature().isJobsEnabled.value;
const missionControlOff = () => !useMissionControlFeature().isMissionControlEnabled.value;

/** The rails Fleet ships. Plugin rails (GitHub, Plugins) come from the plugins' own sidebar contributions. */
railPoint.contribute("core", [
  // Fleet (mission control) comes first and leaves the rail while it's switched off, like Board. It owns its context:
  // the fleet-menu (Needs you, one entry per machine) beside the selected view; off, the page's notice fills the area.
  { id: "fleet", label: "Fleet", icon: Radar, to: "/fleet", placement: "top", order: -10, panel: FleetNavPanel, hidden: missionControlOff, noPanel: missionControlOff },
  { id: "sessions", label: "Sessions", icon: MessageSquare, to: "/", placement: "top", order: 0, panel: SessionsPanel },
  // Board leaves the rail while it's switched off. Workflows stays, dimmed; with either off, the page's "turn it on"
  // notice fills the whole context area (noPanel), so no other view's panel sits beside it.
  { id: "board", label: "Board", icon: LayoutGrid, to: "/board", placement: "top", order: 10, panel: BoardControlsPanel, hidden: boardOff, noPanel: boardOff },
  { id: "workflows", label: "Workflows", icon: Workflow, to: "/workflows", placement: "top", order: 20, panel: WorkflowsNavPanel, off: workflowsOff, noPanel: workflowsOff },
  // Jobs owns its context: the jobs-list (Running, Queued, Done) beside the selected job. It leaves the rail while it's
  // switched off, like Board.
  { id: "jobs", label: "Jobs", icon: ListTodo, to: "/jobs", placement: "top", order: 25, panel: JobsNavPanel, hidden: jobsOff },
  { id: "automations", label: "Automations", icon: Zap, to: "/automations", placement: "top", order: 30, panel: AutomationsContextPanel },
  // Analytics owns its context: the analytics-menu (sections, filters, export) beside the selected section.
  { id: "analytics", label: "Analytics", icon: BarChart3, to: "/analytics", placement: "secondary", order: 0, panel: AnalyticsNavPanel },
  { id: "settings", label: "Settings", icon: Settings, to: "/settings", placement: "bottom", order: 40, panel: SettingsContextPanel },
]);
