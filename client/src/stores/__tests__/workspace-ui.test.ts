import { beforeEach, describe, expect, it } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createGitHubSessionSourcePreset } from "@/lib/github-session-source";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";

describe("useWorkspaceUiStore", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  function createPreset(number: number, title: string) {
    return createGitHubSessionSourcePreset({
      sourceType: "github-issue",
      owner: "acme",
      repo: "weave",
      number,
      title,
      body: "body",
      htmlUrl: `https://github.com/acme/weave/issues/${number}`,
      repoFullName: "acme/weave",
      suggestedBranch: null,
    });
  }

  it("holds a GitHub issue for the New Session page until the page takes it", () => {
    const store = useWorkspaceUiStore();
    const preset = createPreset(42, "Fix flaky create session flow");

    store.setNewSessionInitialSource(preset);
    expect(store.newSessionInitialSource).toEqual(preset);

    store.setNewSessionInitialSource(null);
    expect(store.newSessionInitialSource).toBeNull();
  });

  describe("new session draft", () => {
    const blank = {
      message: "",
      folder: null,
      hasChosenFolder: false,
      workspace: { kind: "new" as const },
      baseBranch: null,
      fetchOrigin: true,
      branchName: "",
      title: "",
      tags: "",
      projectId: null,
      harnessType: "opencode",
      gitHubPreset: null,
    };

    it("has a sidebar row while the page is open, titled from the message as it's typed", () => {
      const store = useWorkspaceUiStore();

      const { draft, restored } = store.openNewSessionDraft(blank);

      expect(restored).toBe(false);
      expect(store.newSessionDraftRow).toMatchObject({ title: "", projectId: null, isStarting: false });

      draft.message = "Fix the login redirect\nIt loops";
      draft.projectId = "project-1";

      expect(store.newSessionDraftRow).toMatchObject({ title: "Fix the login redirect", projectId: "project-1" });
    });

    it("prefers a typed title, then the GitHub issue's", () => {
      const store = useWorkspaceUiStore();
      const { draft } = store.openNewSessionDraft(blank);

      draft.gitHubPreset = createPreset(42, "Login loops");
      expect(store.newSessionDraftRow?.title).toBe("Login loops");

      draft.title = "Login work";
      expect(store.newSessionDraftRow?.title).toBe("Login work");
    });

    it("keeps a draft with a message when you leave, and gives it back", () => {
      const store = useWorkspaceUiStore();
      const { draft } = store.openNewSessionDraft(blank);
      draft.message = "Half a thought";
      draft.folder = { kind: "directory", path: "/tmp/notes" };

      store.leaveNewSessionDraft();

      expect(store.isNewSessionPageOpen).toBe(false);
      expect(store.newSessionDraftRow?.title).toBe("Half a thought");

      const reopened = store.openNewSessionDraft(blank);
      expect(reopened.restored).toBe(true);
      expect(reopened.draft.message).toBe("Half a thought");
      expect(reopened.draft.folder).toEqual({ kind: "directory", path: "/tmp/notes" });
    });

    it("drops an empty draft, and its row, when you leave", () => {
      const store = useWorkspaceUiStore();
      const { draft } = store.openNewSessionDraft(blank);
      draft.message = "   ";

      store.leaveNewSessionDraft();

      expect(store.newSessionDraftRow).toBeNull();
      expect(store.openNewSessionDraft(blank).restored).toBe(false);
    });

    it("keeps a draft that is starting a session", () => {
      const store = useWorkspaceUiStore();
      const { draft } = store.openNewSessionDraft(blank);
      draft.isStarting = true;

      store.leaveNewSessionDraft();

      expect(store.newSessionDraftRow?.isStarting).toBe(true);
    });

    it("hands its row over to the session it became", () => {
      const store = useWorkspaceUiStore();
      store.openNewSessionDraft(blank);
      const rowKey = store.newSessionDraftRow?.key;

      store.handOffNewSessionDraft("session-1");

      expect(store.newSessionDraftRow).toBeNull();
      expect(store.sessionRowKeys["session-1"]).toBe(rowKey);
      expect(store.openNewSessionDraft(blank).draft.message).toBe("");
      expect(store.newSessionDraftRow?.key).not.toBe(rowKey);
    });
  });
});
