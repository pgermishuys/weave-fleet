import { defineStore } from "pinia";
import { computed, reactive, shallowRef } from "vue";
import type { GitHubSessionSourcePreset } from "@/lib/github-session-source";
import { liveMachineKey } from "@/lib/saved-per-machine";
import { titleFromMessage, type NewSessionFolder, type NewSessionWorkspace } from "@/lib/new-session-request";

/** What's on the New Session page, kept while you look at something else. */
export interface NewSessionDraft {
  /** Keys the draft's sidebar row; the session it becomes keeps it, so the row doesn't move. */
  rowKey: string;
  message: string;
  folder: NewSessionFolder | null;
  /** The folder came from the person (or a restored draft), so defaults arriving later don't replace it. */
  hasChosenFolder: boolean;
  workspace: NewSessionWorkspace;
  /** Where a new worktree starts; null for the repository's default. */
  baseBranch: string | null;
  /** Fetch an `origin/…` base first. */
  fetchOrigin: boolean;
  /** Branch for a new worktree instead of the one named from the message; empty for that one. */
  branchName: string;
  title: string;
  tags: string;
  projectId: string | null;
  harnessType: string;
  /** The profile picked for this session; null follows the harness's default. */
  harnessProfileId: string | null;
  /** The agent to start with; empty for the folder's default. */
  agent: string;
  /** The model to start with, as a model selection key; empty for the agent's default. */
  model: string;
  /** The agent or model came from the person, so the folder's remembered ones don't replace them. */
  hasChosenAgentOrModel: boolean;
  gitHubPreset: GitHubSessionSourcePreset | null;
  /** Set between Enter and the session existing. */
  isStarting: boolean;
}

export interface NewSessionDraftRow {
  key: string;
  /** Empty while nothing is typed. */
  title: string;
  projectId: string | null;
  isStarting: boolean;
}

let draftCount = 0;

function hasContent(draft: NewSessionDraft): boolean {
  return draft.message.trim().length > 0 || draft.gitHubPreset !== null || draft.isStarting;
}

export const useWorkspaceUiStore = defineStore("workspace-ui", () => {
  const inlineToolDiffs = shallowRef(false);
  /** A GitHub issue or pull request handed to the New Session page ("start session"). */
  const newSessionInitialSource = shallowRef<GitHubSessionSourcePreset | null>(null);
  const newSessionDraft = shallowRef<NewSessionDraft | null>(null);
  /** Whether the New Session page is showing (a session started from it opens only if it is). */
  const isNewSessionPageOpen = shallowRef(false);
  /** Session id → the draft row key it took over. */
  const sessionRowKeys = shallowRef<Readonly<Record<string, string>>>({});
  /**
   * The machine the draft starts on, as a machine key (`home` or a machine's id); null for the machine the app is
   * working in. A draft keeps the machine picked for it; a new one starts on the live machine.
   */
  const newSessionMachine = shallowRef<string | null>(null);
  // Set while the page is rebuilt for another machine, so leaving it doesn't drop the draft.
  let switchingMachine = false;

  const newSessionDraftRow = computed<NewSessionDraftRow | null>(() => {
    const draft = newSessionDraft.value;
    if (!draft) {
      return null;
    }

    return {
      key: draft.rowKey,
      title: titleFromMessage(draft.title) || titleFromMessage(draft.message) || draft.gitHubPreset?.title.trim() || "",
      projectId: draft.projectId,
      isStarting: draft.isStarting,
    };
  });

  function setInlineToolDiffs(enabled: boolean): void {
    inlineToolDiffs.value = enabled;
  }

  function toggleInlineToolDiffs(): void {
    inlineToolDiffs.value = !inlineToolDiffs.value;
  }

  function setNewSessionInitialSource(source: GitHubSessionSourcePreset | null): void {
    newSessionInitialSource.value = source;
  }

  /**
   * The draft the New Session page edits: the one left from last time, or a fresh one.
   * Returned reactive, so the page's edits show in the sidebar row as they're made.
   */
  function openNewSessionDraft(initial: Omit<NewSessionDraft, "rowKey" | "isStarting">): { draft: NewSessionDraft; restored: boolean } {
    isNewSessionPageOpen.value = true;
    switchingMachine = false;
    const existing = newSessionDraft.value;
    if (existing) {
      return { draft: existing, restored: true };
    }

    draftCount += 1;
    const draft = reactive<NewSessionDraft>({ ...initial, rowKey: `new-session-draft-${draftCount}`, isStarting: false });
    newSessionDraft.value = draft;
    return { draft, restored: false };
  }

  /** Leaving the page: a draft with nothing in it goes; one with a message stays, with its row. */
  function leaveNewSessionDraft(): void {
    isNewSessionPageOpen.value = false;
    if (switchingMachine) {
      return;
    }
    const draft = newSessionDraft.value;
    if (draft && !hasContent(draft)) {
      newSessionDraft.value = null;
    }
    if (!newSessionDraft.value) {
      newSessionMachine.value = null;
    }
  }

  /**
   * Starts the draft on another machine (a machine key; null for the live one). What belongs to the old machine goes:
   * its folder, worktree, branch, project, harness and agent. The message, title, tags and any attached issue stay.
   * The page is rebuilt for the new machine.
   */
  function setNewSessionMachine(picked: string | null): void {
    const machineKey = picked === liveMachineKey() ? null : picked;
    if (machineKey === newSessionMachine.value) {
      return;
    }
    const draft = newSessionDraft.value;
    if (draft) {
      Object.assign(draft, {
        folder: null,
        hasChosenFolder: false,
        workspace: { kind: "new" },
        baseBranch: null,
        fetchOrigin: true,
        branchName: "",
        projectId: null,
        harnessType: "",
        harnessProfileId: null,
        agent: "",
        model: "",
        hasChosenAgentOrModel: false,
      } satisfies Partial<NewSessionDraft>);
    }
    switchingMachine = isNewSessionPageOpen.value;
    newSessionMachine.value = machineKey;
  }

  /** The draft became a session: its row now shows that session, in the same place. */
  function handOffNewSessionDraft(sessionId: string): void {
    const draft = newSessionDraft.value;
    if (!draft) {
      return;
    }

    sessionRowKeys.value = { ...sessionRowKeys.value, [sessionId]: draft.rowKey };
    newSessionDraft.value = null;
    newSessionMachine.value = null;
  }

  return {
    inlineToolDiffs,
    newSessionInitialSource,
    newSessionDraft,
    newSessionDraftRow,
    isNewSessionPageOpen,
    sessionRowKeys,
    newSessionMachine,
    setInlineToolDiffs,
    toggleInlineToolDiffs,
    setNewSessionInitialSource,
    openNewSessionDraft,
    leaveNewSessionDraft,
    handOffNewSessionDraft,
    setNewSessionMachine,
  };
});
