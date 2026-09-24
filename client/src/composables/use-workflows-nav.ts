import { computed, effectScope, ref, shallowRef, watch } from "vue";
import type { NewSessionFolder } from "@/lib/new-session-request";
import type { DraftedWorkflow } from "@/lib/workflow-draft";
import type { Workflow, WorkflowLibrary } from "@/lib/workflows";
import { useWorkflowsStore } from "@/stores/workflows";

const BUILD_A_FEATURE = "builtin:build-a-feature";
/** The Library row, and the active id, of a drafted workflow that hasn't been saved yet. */
export const DRAFT_ID = "draft:new";

const activeWorkflowId = ref<string>(BUILD_A_FEATURE);
/** The repository the Run box runs in; its `.weave/workflows` are listed under "This repo". */
const folder = ref<NewSessionFolder | null>(null);
const hasChosenFolder = ref(false);
const library = shallowRef<WorkflowLibrary | null>(null);
const libraryError = shallowRef<string | null>(null);
const isLoadingLibrary = shallowRef(false);
/** A repository's workflow shows the run view (steps, recent runs, the Run box) instead of the designer: Try it. */
const showingRuns = shallowRef(false);
/** New workflow, or Duplicate when a built-in is given. */
const creating = shallowRef<{ source: { id: string; name: string } | null } | null>(null);
/** The workflow open in the designer with unsaved edits, so its row says Edited. */
const unsavedWorkflowId = shallowRef<string | null>(null);
/** Asking the model for a draft: from a session (its title) or from a description. Error when it failed. */
const drafting = shallowRef<{ sessionTitle: string | null; error: string | null; retry: () => void } | null>(null);
/** The draft the model gave, until the editor takes it. */
const drafted = shallowRef<DraftedWorkflow | null>(null);
let draftController: AbortController | null = null;
/** Asked before another workflow opens, so unsaved edits aren't dropped without a word. */
let leaveGuard: (() => boolean) | null = null;
let watching = false;
/** Module-level, like the state it reads: made in a component's setup, it would stop when that component unmounts. */
const repositoryPath = computed(() => (folder.value?.kind === "repository" ? folder.value.path : null));
let generation = 0;

/**
 * What the Workflows page shows, shared by its list and its detail: the library for the Run box's repository, and
 * which workflow is open.
 */
export function useWorkflowsNav() {
  const store = useWorkflowsStore();


  async function reload(): Promise<void> {
    const mine = ++generation;
    isLoadingLibrary.value = true;
    libraryError.value = null;
    try {
      const next = await store.loadLibrary(repositoryPath.value);
      if (mine !== generation) return;
      library.value = next;
      if (activeWorkflowId.value !== DRAFT_ID && !next.workflows.some((workflow) => workflow.id === activeWorkflowId.value))
        activeWorkflowId.value = next.workflows[0]?.id ?? BUILD_A_FEATURE;
    } catch (error) {
      if (mine === generation) libraryError.value = error instanceof Error ? error.message : "Couldn't load the workflows.";
    } finally {
      if (mine === generation) isLoadingLibrary.value = false;
    }
  }

  if (!watching) {
    watching = true;
    // In a scope of its own: made in whichever component asks first (a session row can), it has to outlive it.
    effectScope(true).run(() => watch(repositoryPath, () => void reload()));
  }

  const builtIns = computed<Workflow[]>(() => library.value?.workflows.filter((w) => w.builtIn) ?? []);
  const repoWorkflows = computed<Workflow[]>(() => library.value?.workflows.filter((w) => !w.builtIn) ?? []);
  const activeWorkflow = computed<Workflow | null>(
    () => library.value?.workflows.find((w) => w.id === activeWorkflowId.value) ?? null);

  function setActiveWorkflow(id: string): void {
    if (id === activeWorkflowId.value) return;
    if (leaveGuard && !leaveGuard()) return;
    if (activeWorkflowId.value === DRAFT_ID) forgetDraft();
    activeWorkflowId.value = id;
    showingRuns.value = false;
  }

  /**
   * Asks the model for a draft and opens it, unsaved, once it comes. Only one at a time: another ask cancels the
   * first. The server deletes whatever it made for the question either way.
   */
  async function startDraft(sessionTitle: string | null, ask: (signal: AbortSignal) => Promise<DraftedWorkflow>): Promise<void> {
    if (activeWorkflowId.value !== DRAFT_ID && leaveGuard && !leaveGuard()) return;
    draftController?.abort();
    const controller = new AbortController();
    draftController = controller;
    drafted.value = null;
    drafting.value = { sessionTitle, error: null, retry: () => void startDraft(sessionTitle, ask) };
    activeWorkflowId.value = DRAFT_ID;
    showingRuns.value = false;
    try {
      const result = await ask(controller.signal);
      if (draftController !== controller) return;
      setFolder({ kind: "repository", path: result.repository }, true);
      drafted.value = result;
      drafting.value = null;
    } catch (error) {
      if (draftController !== controller || controller.signal.aborted) return;
      drafting.value = { ...drafting.value!, error: error instanceof Error ? error.message : "Couldn't draft the workflow." };
    } finally {
      if (draftController === controller) draftController = null;
    }
  }

  /** Save as workflow… on a session. */
  function draftFromSession(sessionId: string, sessionTitle: string): Promise<void> {
    return startDraft(sessionTitle, (signal) => store.draftFromSession(sessionId, signal));
  }

  /** New workflow → Describe it. */
  function draftFromDescription(directory: string, description: string, harnessType: string | null): Promise<void> {
    return startDraft(null, (signal) => store.draftFromDescription({ directory, description, harnessType }, signal));
  }

  /** Cancel, while the model is being asked: back to the workflow that was open before. */
  function cancelDraft(): void {
    forgetDraft();
    activeWorkflowId.value = library.value?.workflows[0]?.id ?? BUILD_A_FEATURE;
  }

  function forgetDraft(): void {
    draftController?.abort();
    draftController = null;
    drafting.value = null;
    drafted.value = null;
  }

  /** The draft was saved: it's the repository's workflow now. */
  function draftSaved(workflowId: string): void {
    forgetDraft();
    activeWorkflowId.value = workflowId;
  }

  /** The editor says whether it's all right to leave it (nothing unsaved, or the user said so). */
  function setLeaveGuard(guard: (() => boolean) | null): void {
    leaveGuard = guard;
  }

  function showRuns(on: boolean): void {
    showingRuns.value = on;
  }

  function startCreate(source: { id: string; name: string } | null = null): void {
    creating.value = { source };
  }

  function endCreate(): void {
    creating.value = null;
  }

  function setFolder(next: NewSessionFolder, chosen: boolean): void {
    folder.value = next;
    hasChosenFolder.value ||= chosen;
  }

  return {
    activeWorkflowId,
    activeWorkflow,
    builtIns,
    repoWorkflows,
    library,
    libraryError,
    isLoadingLibrary,
    folder,
    hasChosenFolder,
    repositoryPath,
    showingRuns,
    creating,
    unsavedWorkflowId,
    drafting,
    drafted,
    reload,
    setActiveWorkflow,
    setFolder,
    setLeaveGuard,
    showRuns,
    startCreate,
    endCreate,
    draftFromSession,
    draftFromDescription,
    cancelDraft,
    draftSaved,
  };
}
