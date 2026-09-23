import { computed, ref, shallowRef, watch } from "vue";
import type { NewSessionFolder } from "@/lib/new-session-request";
import type { Workflow, WorkflowLibrary } from "@/lib/workflows";
import { useWorkflowsStore } from "@/stores/workflows";

const BUILD_A_FEATURE = "builtin:build-a-feature";

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
/** Asked before another workflow opens, so unsaved edits aren't dropped without a word. */
let leaveGuard: (() => boolean) | null = null;
let watching = false;
let generation = 0;

/**
 * What the Workflows page shows, shared by its list and its detail: the library for the Run box's repository, and
 * which workflow is open.
 */
export function useWorkflowsNav() {
  const store = useWorkflowsStore();

  const repositoryPath = computed(() => (folder.value?.kind === "repository" ? folder.value.path : null));

  async function reload(): Promise<void> {
    const mine = ++generation;
    isLoadingLibrary.value = true;
    libraryError.value = null;
    try {
      const next = await store.loadLibrary(repositoryPath.value);
      if (mine !== generation) return;
      library.value = next;
      if (!next.workflows.some((workflow) => workflow.id === activeWorkflowId.value))
        activeWorkflowId.value = next.workflows[0]?.id ?? BUILD_A_FEATURE;
    } catch (error) {
      if (mine === generation) libraryError.value = error instanceof Error ? error.message : "Couldn't load the workflows.";
    } finally {
      if (mine === generation) isLoadingLibrary.value = false;
    }
  }

  if (!watching) {
    watching = true;
    watch(repositoryPath, () => void reload());
  }

  const builtIns = computed<Workflow[]>(() => library.value?.workflows.filter((w) => w.builtIn) ?? []);
  const repoWorkflows = computed<Workflow[]>(() => library.value?.workflows.filter((w) => !w.builtIn) ?? []);
  const activeWorkflow = computed<Workflow | null>(
    () => library.value?.workflows.find((w) => w.id === activeWorkflowId.value) ?? null);

  function setActiveWorkflow(id: string): void {
    if (id === activeWorkflowId.value) return;
    if (leaveGuard && !leaveGuard()) return;
    activeWorkflowId.value = id;
    showingRuns.value = false;
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
    reload,
    setActiveWorkflow,
    setFolder,
    setLeaveGuard,
    showRuns,
    startCreate,
    endCreate,
  };
}
