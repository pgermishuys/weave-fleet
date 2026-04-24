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

  it("clears dialog context when the dialog closes", () => {
    const store = useWorkspaceUiStore();
    const preset = createPreset(42, "Fix flaky create session flow");

    store.openNewSessionDialog("project-1", preset);

    expect(store.newSessionDialogOpen).toBe(true);
    expect(store.newSessionDialogProjectId).toBe("project-1");
    expect(store.newSessionDialogInitialSource).toEqual(preset);

    store.setNewSessionDialogOpen(false);

    expect(store.newSessionDialogOpen).toBe(false);
    expect(store.newSessionDialogProjectId).toBeNull();
    expect(store.newSessionDialogInitialSource).toBeNull();
  });

  it("replaces stale GitHub context after close and reopen", () => {
    const store = useWorkspaceUiStore();
    const firstPreset = createPreset(42, "First issue");
    const secondPreset = createPreset(73, "Second issue");

    store.openNewSessionDialog("project-1", firstPreset);
    store.closeNewSessionDialog();

    expect(store.newSessionDialogOpen).toBe(false);
    expect(store.newSessionDialogInitialSource).toBeNull();

    store.openNewSessionDialog(null, secondPreset);

    expect(store.newSessionDialogOpen).toBe(true);
    expect(store.newSessionDialogProjectId).toBeNull();
    expect(store.newSessionDialogInitialSource).toEqual(secondPreset);
    expect(store.newSessionDialogInitialSource).not.toEqual(firstPreset);
  });
});
