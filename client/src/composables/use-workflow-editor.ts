import { computed, markRaw, shallowRef } from "vue";
import {
  cloneDraft,
  sameDraft,
  slugOf,
  type DraftedWorkflow,
  type WorkflowCheck,
  type WorkflowComment,
  type WorkflowDraft,
  type WorkflowFile,
} from "@/lib/workflow-draft";
import { useWorkflowsStore, WorkflowFileChangedError } from "@/stores/workflows";

export type WorkflowEditorView = "designer" | "file";

/** How long after the last keystroke the server checks the file. */
export const CHECK_DELAY_MS = 300;

export const DESIGNER_BLOCKED_MESSAGE = "The designer can't show this file as it is. Fix the errors in the File view to use the designer.";

/**
 * One open workflow file: the designer's draft and the File view's text, the server's check of whichever was edited
 * last, and saving. The File view's text is saved exactly as typed; only a draft edited in the designer is written in
 * Fleet's layout, which removes comments, so the first such save asks first.
 */
export function useWorkflowEditor() {
  const store = useWorkflowsStore();

  const directory = shallowRef<string | null>(null);
  const file = shallowRef<WorkflowFile | null>(null);
  const view = shallowRef<WorkflowEditorView>("designer");
  const draft = shallowRef<WorkflowDraft | null>(null);
  const text = shallowRef("");
  /** Which view was edited last, so Save knows what to send; null when nothing has changed since opening or saving. */
  const source = shallowRef<"draft" | "text" | null>(null);
  const check = shallowRef<WorkflowCheck | null>(null);
  const isChecking = shallowRef(false);
  const isSaving = shallowRef(false);
  const loadError = shallowRef<string | null>(null);
  const saveError = shallowRef<string | null>(null);
  /** Why Designer didn't open: the File view's text can't be shown there without losing some of it. */
  const designerBlocked = shallowRef<string | null>(null);
  /** The comments in the file's text as it is now: what a save from the designer would remove. */
  const comments = shallowRef<WorkflowComment[]>([]);
  const commentsConfirmed = shallowRef(false);
  const askingToRemoveComments = shallowRef(false);
  /** The file changed on disk since it was opened: Reload or Keep mine. */
  const conflict = shallowRef<string | null>(null);
  /** A drafted workflow that has never been saved: there's no file yet, and Save creates it. */
  const drafted = shallowRef<DraftedWorkflow | null>(null);

  let savedText = "";
  /** The text as it was when the designer's edits began: what Edit in File view goes back to, comments and all. */
  let textBeforeDesigner: string | null = null;
  let savedDraft: WorkflowDraft | null = null;
  let timer: ReturnType<typeof setTimeout> | null = null;
  let generation = 0;
  let inFlight: Promise<void> | null = null;
  let controller: AbortController | null = null;

  const errors = computed(() => check.value?.errors ?? []);
  const isDirty = computed(() => {
    if (drafted.value) return true;
    if (source.value === "draft") return !sameDraft(draft.value, savedDraft);
    if (source.value === "text") return text.value !== savedText;
    return false;
  });
  const canSave = computed(() => isDirty.value && !isChecking.value && !isSaving.value && errors.value.length === 0 && check.value !== null);

  function reset(opened: WorkflowFile): void {
    file.value = opened;
    check.value = opened.check;
    text.value = opened.check.text;
    savedText = opened.check.text;
    draft.value = opened.check.draft ? cloneDraft(opened.check.draft) : null;
    savedDraft = draft.value ? cloneDraft(draft.value) : null;
    comments.value = opened.check.comments;
    source.value = null;
    textBeforeDesigner = null;
    designerBlocked.value = null;
    saveError.value = null;
  }

  async function open(repository: string, workflowId: string): Promise<void> {
    cancelCheck();
    drafted.value = null;
    directory.value = repository;
    loadError.value = null;
    commentsConfirmed.value = false;
    conflict.value = null;
    try {
      const opened = await store.openFile(repository, workflowId);
      reset(opened);
      view.value = opened.check.draft ? "designer" : "file";
    } catch (error) {
      file.value = null;
      loadError.value = error instanceof Error ? error.message : "Couldn't open the workflow file.";
    }
  }

  /** A file New or Duplicate just made: open it without reading it again. */
  function adopt(repository: string, created: WorkflowFile): void {
    cancelCheck();
    drafted.value = null;
    directory.value = repository;
    loadError.value = null;
    commentsConfirmed.value = false;
    conflict.value = null;
    reset(created);
    view.value = created.check.draft ? "designer" : "file";
  }

  /**
   * A drafted workflow, unsaved: its file is the one Save would create, named after the workflow. A draft the designer
   * can show opens there; one with errors opens in the File view, with them.
   */
  function adoptDraft(next: DraftedWorkflow): void {
    cancelCheck();
    directory.value = next.repository;
    loadError.value = null;
    commentsConfirmed.value = false;
    conflict.value = null;
    reset({ workflowId: "", file: draftFile(next.check.draft?.name ?? ""), hash: "", check: next.check });
    drafted.value = next;
    view.value = next.check.draft && next.check.errors.length === 0 ? "designer" : "file";
  }

  function draftFile(name: string): string {
    return `.weave/workflows/${slugOf(name)}.yaml`;
  }

  /** The file a drafted workflow would be saved as, following its name as it's edited. */
  const fileName = computed(() => {
    if (!drafted.value) return file.value?.file ?? "";
    const name = source.value === "text" ? check.value?.draft?.name : draft.value?.name;
    return draftFile(name ?? draft.value?.name ?? "");
  });

  function cancelCheck(): void {
    if (timer) clearTimeout(timer);
    timer = null;
    controller?.abort();
    controller = null;
    generation++;
    isChecking.value = false;
  }

  function scheduleCheck(): void {
    if (timer) clearTimeout(timer);
    isChecking.value = true;
    timer = setTimeout(() => {
      timer = null;
      void runCheck();
    }, CHECK_DELAY_MS);
  }

  function runCheck(): Promise<void> {
    controller?.abort();
    controller = new AbortController();
    const mine = ++generation;
    const body = source.value === "draft" && draft.value ? { draft: draft.value } : { text: text.value };
    const signal = controller.signal;
    isChecking.value = true;
    inFlight = (async () => {
      try {
        const result = await store.check(body, signal);
        if (mine !== generation) return;
        check.value = result;
        if ("text" in body) comments.value = result.comments;
      } catch (error) {
        if (mine !== generation || signal.aborted) return;
        saveError.value = error instanceof Error ? error.message : "Couldn't check the workflow.";
      } finally {
        if (mine === generation) isChecking.value = false;
      }
    })();
    return inFlight;
  }

  /** Checks now what's waiting to be checked, so a view switch or a save sees the latest. */
  async function flush(): Promise<void> {
    if (timer) {
      clearTimeout(timer);
      timer = null;
      await runCheck();
    } else if (inFlight) {
      await inFlight;
    }
  }

  function editDraft(next: WorkflowDraft): void {
    if (source.value !== "draft") textBeforeDesigner = text.value;
    draft.value = next;
    source.value = "draft";
    saveError.value = null;
    scheduleCheck();
  }

  function editText(next: string): void {
    if (next === text.value) return;
    text.value = next;
    source.value = "text";
    saveError.value = null;
    designerBlocked.value = null;
    scheduleCheck();
  }

  async function setView(next: WorkflowEditorView): Promise<void> {
    if (next === view.value) return;
    await flush();
    if (next === "file") {
      // What the designer's edits write; otherwise the text as it is, comments and all.
      if (source.value === "draft" && check.value) text.value = check.value.text;
      designerBlocked.value = null;
      view.value = "file";
      return;
    }

    if (source.value === "text") {
      const shown = check.value?.draft;
      if (!shown) {
        designerBlocked.value = DESIGNER_BLOCKED_MESSAGE;
        return;
      }
      draft.value = cloneDraft(shown);
    } else if (!draft.value) {
      designerBlocked.value = DESIGNER_BLOCKED_MESSAGE;
      return;
    }
    designerBlocked.value = null;
    view.value = "designer";
  }

  /**
   * Saves what was edited last. A draft from the designer is written in Fleet's layout, so a file with comments asks
   * first (once). Resolves true when the file was saved.
   */
  async function save(force = false): Promise<boolean> {
    if (!file.value || !directory.value) return false;
    await flush();
    if (!force && !canSave.value) return false;

    const fromDesigner = source.value === "draft" && draft.value !== null;
    if (fromDesigner && comments.value.length > 0 && !commentsConfirmed.value) {
      askingToRemoveComments.value = true;
      return false;
    }

    isSaving.value = true;
    saveError.value = null;
    if (drafted.value) return saveDrafted(fromDesigner);
    try {
      const saved = await store.saveFile({
        directory: directory.value,
        workflowId: file.value.workflowId,
        hash: file.value.hash,
        ...(fromDesigner ? { draft: draft.value! } : { text: text.value }),
        force,
      });
      const keepView = view.value;
      reset(saved);
      if (!saved.check.draft && keepView === "designer") view.value = "file";
      conflict.value = null;
      return true;
    } catch (error) {
      if (error instanceof WorkflowFileChangedError) conflict.value = error.message;
      else saveError.value = error instanceof Error ? error.message : "Couldn't save the workflow file.";
      return false;
    } finally {
      isSaving.value = false;
    }
  }

  /** The first save of a drafted workflow: the file is created, under New's rules for names. */
  async function saveDrafted(fromDesigner: boolean): Promise<boolean> {
    try {
      const created = await store.createDrafted(directory.value!, fromDesigner ? { draft: draft.value! } : { text: text.value });
      drafted.value = null;
      reset(created);
      if (!created.check.draft) view.value = "file";
      return true;
    } catch (error) {
      saveError.value = error instanceof Error ? error.message : "Couldn't save the workflow file.";
      return false;
    } finally {
      isSaving.value = false;
    }
  }

  /** Save and remove them, from the comments confirmation. */
  function confirmRemoveComments(): Promise<boolean> {
    askingToRemoveComments.value = false;
    commentsConfirmed.value = true;
    return save();
  }

  /**
   * Edit in File view, from the banner or the confirmation: the text as it was, comments and all. Edits made in the
   * designer since would be written in Fleet's layout, so they're dropped: make them in the File view instead.
   */
  async function keepComments(): Promise<void> {
    askingToRemoveComments.value = false;
    if (source.value !== "draft" || textBeforeDesigner === null) {
      await setView("file");
      return;
    }

    cancelCheck();
    text.value = textBeforeDesigner;
    textBeforeDesigner = null;
    source.value = text.value === savedText ? null : "text";
    if (source.value === null) draft.value = savedDraft ? cloneDraft(savedDraft) : null;
    designerBlocked.value = null;
    view.value = "file";
    await runCheck();
  }

  /** Reload, from the conflict dialog: what's on disk now, and the edits are dropped. */
  async function reload(): Promise<void> {
    conflict.value = null;
    if (file.value && directory.value) await open(directory.value, file.value.workflowId);
  }

  /** Keep mine, from the conflict dialog: saves over the newer file. */
  function keepMine(): Promise<boolean> {
    conflict.value = null;
    return save(true);
  }

  // Raw, so passing it as a prop doesn't unwrap its refs.
  return markRaw({
    directory,
    file,
    view,
    draft,
    text,
    source,
    check,
    errors,
    comments,
    isChecking,
    isSaving,
    isDirty,
    canSave,
    loadError,
    saveError,
    designerBlocked,
    askingToRemoveComments,
    conflict,
    drafted,
    fileName,
    open,
    adopt,
    adoptDraft,
    editDraft,
    editText,
    setView,
    flush,
    save,
    confirmRemoveComments,
    keepComments,
    reload,
    keepMine,
    dispose: cancelCheck,
  });
}

export type WorkflowEditor = ReturnType<typeof useWorkflowEditor>;
