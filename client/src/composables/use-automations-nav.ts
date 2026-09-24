import { reactive, ref } from "vue";
import type { When } from "@/lib/automation-schedule";
import type { NewSessionFolder, NewSessionWorkspace } from "@/lib/new-session-request";

export type AutomationViewMode = "list" | "create" | "edit";

/** What the automation composer is editing: a new automation's draft, or an open automation's changes. */
export interface AutomationComposerState {
  /** What it should do, and usually when. */
  text: string;
  /** A schedule chosen in the When menu, which takes over from the words in the text. */
  manualWhen: When | null;
  /** The text said "on Friday" and "Just once" was chosen. */
  forceOnce: boolean;
  folder: NewSessionFolder | null;
  /** Set once someone picked a folder, so the default doesn't replace it. */
  hasChosenFolder: boolean;
  /** "new" is a new worktree for each run; "current" the folder as it is. */
  workspace: NewSessionWorkspace;
  baseBranch: string | null;
  /** "new_session", "same_session", "workflow", or an older automation's target type. */
  targetType: string;
  /** The workflow a "workflow" target runs. */
  workflowId: string | null;
  /** The workflow's optional steps switched on. */
  workflowSteps: string[];
  name: string;
  /** Skip a run while the last one is still going. */
  skip: boolean;
  /** The agent runs go to; empty for the folder's default. */
  agent: string;
  /** The model runs use, as a model selection key; empty for the agent's default. */
  model: string;
  /** The harness the agent and model come from, or a workflow runs on; null for the default harness. */
  harnessType: string | null;
}

/** What "Repeat on a schedule…" in the Workflows Library starts a new automation from. */
export interface WorkflowAutomationSeed {
  workflowId: string;
  /** The repository the Library lists and the Run box runs in. */
  folder: string;
  /** What's typed in the Run box, if anything: the run's request. */
  request: string;
  optionalSteps: string[];
  baseBranch: string | null;
  harnessType: string | null;
}

export function freshComposerState(): AutomationComposerState {
  return {
    text: "",
    manualWhen: null,
    forceOnce: false,
    folder: null,
    hasChosenFolder: false,
    workspace: { kind: "new" },
    baseBranch: null,
    targetType: "new_session",
    workflowId: null,
    workflowSteps: [],
    name: "",
    skip: true,
    agent: "",
    model: "",
    harnessType: null,
  };
}

const activeAutomationId = ref<string | null>(null);
const viewMode = ref<AutomationViewMode>("list");
/** The new automation being written. It survives leaving the page, and the sidebar's draft row shows it. */
const draft = reactive<AutomationComposerState>(freshComposerState());
/** A session whose first message and folder the next new automation starts from ("Repeat on a schedule…"). */
const seedSessionId = ref<string | null>(null);

export function useAutomationsNav() {
  function setActiveAutomation(id: string): void {
    activeAutomationId.value = id;
    viewMode.value = "edit";
  }

  function startCreate(): void {
    activeAutomationId.value = null;
    viewMode.value = "create";
  }

  /** A new automation from a session: its first message and folder, with the When still to add. */
  function startCreateFromSession(sessionId: string): void {
    Object.assign(draft, freshComposerState());
    seedSessionId.value = sessionId;
    startCreate();
  }

  /** A new automation that runs a workflow, with the Run box's choices, and When still to add. */
  function startCreateFromWorkflow(seed: WorkflowAutomationSeed): void {
    Object.assign(draft, freshComposerState(), {
      text: seed.request,
      folder: { kind: "repository", path: seed.folder },
      hasChosenFolder: true,
      workspace: { kind: "new" },
      baseBranch: seed.baseBranch,
      targetType: "workflow",
      workflowId: seed.workflowId,
      workflowSteps: [...seed.optionalSteps],
      harnessType: seed.harnessType,
    } satisfies Partial<AutomationComposerState>);
    seedSessionId.value = null;
    startCreate();
  }

  function resetDraft(): void {
    Object.assign(draft, freshComposerState());
  }

  function clearSelection(): void {
    activeAutomationId.value = null;
    viewMode.value = "list";
  }

  return {
    activeAutomationId,
    viewMode,
    draft,
    seedSessionId,
    setActiveAutomation,
    startCreate,
    startCreateFromSession,
    startCreateFromWorkflow,
    resetDraft,
    clearSelection,
  };
}
