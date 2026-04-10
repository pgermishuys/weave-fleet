/**
 * @vitest-environment jsdom
 */

import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { CreateSessionButton } from "@/integrations/github/components/create-session-button";
import { SessionsContext, type SessionsContextValue } from "@/contexts/sessions-context";
import type { ContextSource } from "@/integrations/types";
import type { SessionSourcePreview } from "@/lib/api-types";

const {
  createSessionMock,
  previewSourceMock,
  addSourceToSessionMock,
} = vi.hoisted(() => ({
  createSessionMock: vi.fn(),
  previewSourceMock: vi.fn(),
  addSourceToSessionMock: vi.fn(),
}));

vi.mock("@/hooks/use-create-session", () => ({
  useCreateSession: () => ({
    createSession: createSessionMock,
    isLoading: false,
    error: undefined,
  }),
}));

vi.mock("@/hooks/use-add-source-to-session", () => ({
  useAddSourceToSession: () => ({
    previewSource: previewSourceMock,
    addSourceToSession: addSourceToSessionMock,
    isLoading: false,
    error: undefined,
  }),
}));

vi.mock("@/components/session/new-session-dialog", () => ({
  NewSessionDialog: ({ open, initialTitle, initialSource }: { open: boolean; initialTitle?: string; initialSource?: unknown }) => (open
    ? (
        <div
          data-testid="new-session-dialog"
          data-initial-title={initialTitle}
          data-has-initial-source={initialSource ? "true" : "false"}
        />
      )
    : null),
}));

const preview: SessionSourcePreview = {
  originLabel: "GitHub issue #42",
  content: "## Body\nInvestigate the failing release build.",
  isTruncated: false,
  characterCount: 44,
};

const contextSource: ContextSource = {
  type: "github-issue",
  url: "https://github.com/acme/repo/issues/42",
  title: "Issue #42: Fix release build",
  source: {
    key: {
      providerId: "builtin.github",
      sourceType: "github-issue",
      actionId: "add-to-session",
      contractVersion: 1,
    },
    input: {
      owner: "acme",
      repo: "repo",
      number: 42,
    },
  },
  metadata: {},
};

function makeSession(id: string, title: string): SessionsContextValue["sessions"][number] {
  return {
    instanceId: `${id}-instance`,
    workspaceId: `${id}-workspace`,
    workspaceDirectory: "/tmp/workspace",
    workspaceDisplayName: null,
    isolationStrategy: "existing",
    sessionStatus: "active",
    session: { id, title, time: { created: 0, updated: 0 } },
    instanceStatus: "running",
    parentSessionId: null,
    sourceDirectory: null,
    branch: null,
    activityStatus: "idle",
    lifecycleStatus: "running",
    retentionStatus: "active",
    archivedAt: null,
    typedInstanceStatus: "running",
    isHidden: false,
    projectId: null,
    projectName: null,
  };
}

function renderWithContext(sessions: SessionsContextValue["sessions"]) {
  const refetch = vi.fn();

  render(
    <SessionsContext.Provider
      value={{
        sessions,
        retentionFilter: "active",
        setRetentionFilter: () => {},
        isLoading: false,
        error: undefined,
        refetch,
        summary: null,
        patchSessionTitle: () => {},
        patchWorkspaceDisplayName: () => {},
      }}
    >
      <MemoryRouter initialEntries={["/sessions/current-session?instanceId=current-instance"]}>
        <Routes>
          <Route
            path="/sessions/:id"
            element={<CreateSessionButton contextSource={contextSource} directory="/tmp/repo" />}
          />
        </Routes>
      </MemoryRouter>
    </SessionsContext.Provider>,
  );

  return { refetch };
}

describe("CreateSessionButton", () => {
  beforeEach(() => {
    createSessionMock.mockReset();
    previewSourceMock.mockReset();
    addSourceToSessionMock.mockReset();
    createSessionMock.mockResolvedValue({
      instanceId: "new-instance",
      workspaceId: "workspace-1",
      session: { id: "new-session", title: "New Session", time: { created: 0, updated: 0 } },
    });
    previewSourceMock.mockResolvedValue(preview);
    addSourceToSessionMock.mockResolvedValue(undefined);
  });

  it("previews and adds GitHub context to the current session", async () => {
    const user = userEvent.setup();
    const currentSession = makeSession("current-session", "Current Session");
    const otherSession = makeSession("other-session", "Other Session");
    const { refetch } = renderWithContext([currentSession, otherSession]);

    await user.click(screen.getByRole("button", { name: "Create Session" }));

    await waitFor(() => {
      expect(previewSourceMock).toHaveBeenCalledWith("current-session", contextSource.source);
    });

    expect(await screen.findByText(/Investigate the failing release build\./)).toBeTruthy();

    await user.click(screen.getByRole("button", { name: "Add to Session" }));

    await waitFor(() => {
      expect(addSourceToSessionMock).toHaveBeenCalledWith("current-session", contextSource.source, true);
    });
    expect(refetch).toHaveBeenCalled();
  });

  it("creates a workspace session first, then attaches GitHub context for quick start", async () => {
    const user = userEvent.setup();
    renderWithContext([makeSession("current-session", "Current Session")]);

    await user.click(screen.getByRole("button", { name: "Create Session" }));
    await user.click(screen.getByRole("button", { name: "Use Current Directory" }));

    await waitFor(() => {
      expect(createSessionMock).toHaveBeenCalledWith("/tmp/repo", {
        title: contextSource.title,
      });
    });
    await waitFor(() => {
      expect(addSourceToSessionMock).toHaveBeenCalledWith("new-session", contextSource.source, true);
    });
  });

  it("opens the shared session dialog when choosing workspace source", async () => {
    const user = userEvent.setup();
    renderWithContext([makeSession("current-session", "Current Session")]);

    await user.click(screen.getByRole("button", { name: "Create Session" }));
    await user.click(screen.getByRole("button", { name: "Choose Workspace Source" }));

    const dialog = screen.getByTestId("new-session-dialog");
    expect(dialog).toBeTruthy();
    expect(dialog.getAttribute("data-has-initial-source")).toBe("true");
    expect(dialog.getAttribute("data-initial-title")).toBe(contextSource.title);
  });
});
