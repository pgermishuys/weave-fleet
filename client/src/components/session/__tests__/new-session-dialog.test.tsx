/**
 * @vitest-environment jsdom
 */

import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ComponentProps } from "react";
import { MemoryRouter } from "react-router";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { NewSessionDialog } from "@/components/session/new-session-dialog";
import { SessionsContext, type SessionsContextValue } from "@/contexts/sessions-context";

const createSessionMock = vi.fn();
const refreshRepositoriesMock = vi.fn();

vi.mock("@/hooks/use-create-session", () => ({
  useCreateSession: () => ({
    createSession: createSessionMock,
    isLoading: false,
    error: undefined,
  }),
}));

vi.mock("@/hooks/use-add-source-to-session", () => ({
  useAddSourceToSession: () => ({
    addSourceToSession: vi.fn(),
    isLoading: false,
    error: undefined,
  }),
}));

vi.mock("@/hooks/use-repositories", () => ({
  useRepositories: () => ({
    repositories: [
      { name: "repo-one", path: "/tmp/repo-one", parentRoot: "/tmp" },
    ],
    isLoading: false,
    refresh: refreshRepositoriesMock,
  }),
}));

vi.mock("@/session-sources/use-session-sources", () => ({
  useSessionSources: () => ({
    sources: [
      {
        descriptor: {
          key: {
            providerId: "builtin.repository",
            sourceType: "repository",
            actionId: "start-session",
            contractVersion: 1,
          },
          displayName: "Repository",
          kind: "workspace",
          inputFields: [],
          producesWorkspace: true,
          producesContext: false,
          requiresConfirmation: false,
        },
        label: "Repository",
        description: "Start from a discovered repository.",
        order: 100,
      },
      {
        descriptor: {
          key: {
            providerId: "builtin.local",
            sourceType: "directory",
            actionId: "start-session",
            contractVersion: 1,
          },
          displayName: "Directory",
          kind: "workspace",
          inputFields: [],
          producesWorkspace: true,
          producesContext: false,
          requiresConfirmation: false,
        },
        label: "Directory",
        description: "Start from an existing local directory.",
        order: 110,
      },
    ],
    isLoading: false,
    error: null,
  }),
}));

vi.mock("@/hooks/use-harnesses", () => ({
  useHarnesses: () => ({
    harnesses: [{ type: "opencode", displayName: "OpenCode", available: true }],
    isLoading: false,
  }),
}));

vi.mock("@/hooks/use-projects", () => ({
  useProjects: () => ({
    projects: [],
  }),
}));

vi.mock("@/hooks/use-persisted-state", () => ({
  usePersistedState: (_key: string, initialValue: unknown) => [initialValue, vi.fn()],
}));

function renderDialog(props?: Partial<ComponentProps<typeof NewSessionDialog>>) {
  const sessionsContextValue: SessionsContextValue = {
    sessions: [],
    retentionFilter: "active",
    setRetentionFilter: () => {},
    isLoading: false,
    error: undefined,
    refetch: vi.fn(),
    summary: null,
    patchSessionTitle: () => {},
    patchWorkspaceDisplayName: () => {},
  };

  return render(
    <SessionsContext.Provider value={sessionsContextValue}>
      <MemoryRouter>
        <NewSessionDialog open onOpenChange={() => {}} {...props} />
      </MemoryRouter>
    </SessionsContext.Provider>,
  );
}

describe("NewSessionDialog", () => {
  beforeEach(() => {
    createSessionMock.mockReset();
    refreshRepositoriesMock.mockReset();
  });

  it("renders source picker options from the session source registry", async () => {
    renderDialog();

    expect(screen.getAllByRole("radio", { name: /Repository/i }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole("radio", { name: /Directory/i }).length).toBeGreaterThan(0);
  });

  it("switches to the directory source when selected", async () => {
    const user = userEvent.setup();
    renderDialog();

    await user.click(screen.getAllByRole("radio", { name: /Directory/i })[0]);

    expect(screen.getByLabelText("Directory")).toBeTruthy();
  });
});
