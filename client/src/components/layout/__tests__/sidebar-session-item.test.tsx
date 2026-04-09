import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { describe, expect, it, beforeEach, vi } from "vitest";
import { SidebarSessionItem } from "@/components/layout/sidebar-session-item";
import { SessionsContext, type SessionsContextValue } from "@/contexts/sessions-context";
import type { SessionListItem } from "@/lib/api-types";

const mockNavigate = vi.fn();
const mockDeleteSession = vi.fn<(...args: [string, string]) => Promise<void>>();
const mockArchiveSession = vi.fn<(...args: [string]) => Promise<void>>();
const mockUnarchiveSession = vi.fn<(...args: [string]) => Promise<void>>();

vi.mock("react-router", async (importOriginal) => {
  const actual = await importOriginal<typeof import("react-router")>();
  return {
    ...actual,
    useNavigate: () => mockNavigate,
  };
});

vi.mock("@/hooks/use-rename-session", () => ({
  useRenameSession: () => ({ renameSession: vi.fn() }),
}));

vi.mock("@/hooks/use-terminate-session", () => ({
  useTerminateSession: () => ({ terminateSession: vi.fn() }),
}));

vi.mock("@/hooks/use-abort-session", () => ({
  useAbortSession: () => ({ abortSession: vi.fn() }),
}));

vi.mock("@/hooks/use-delete-session", () => ({
  useDeleteSession: () => ({ deleteSession: mockDeleteSession, isDeleting: false }),
}));

vi.mock("@/hooks/use-archive-session", () => ({
  useArchiveSession: () => ({ archiveSession: mockArchiveSession, isArchiving: false }),
}));

vi.mock("@/hooks/use-unarchive-session", () => ({
  useUnarchiveSession: () => ({ unarchiveSession: mockUnarchiveSession, isUnarchiving: false }),
}));

vi.mock("@/hooks/use-resume-session", () => ({
  useResumeSession: () => ({ resumeSession: vi.fn() }),
}));

vi.mock("@/hooks/use-open-directory", () => ({
  useOpenDirectory: () => ({ openDirectory: vi.fn() }),
}));

vi.mock("@/hooks/use-move-session", () => ({
  useMoveSession: () => ({ moveSession: vi.fn() }),
}));

vi.mock("@/components/ui/context-menu", () => ({
  ContextMenu: ({ children }: { children: React.ReactNode }) => <>{children}</>,
  ContextMenuTrigger: ({ children }: { children: React.ReactNode }) => <>{children}</>,
  ContextMenuContent: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
  ContextMenuItem: ({ children, onClick, ...props }: React.ButtonHTMLAttributes<HTMLButtonElement>) => (
    <button type="button" onClick={onClick} {...props}>
      {children}
    </button>
  ),
  ContextMenuSeparator: () => null,
  ContextMenuSub: ({ children }: { children: React.ReactNode }) => <>{children}</>,
  ContextMenuSubContent: ({ children }: { children: React.ReactNode }) => <>{children}</>,
  ContextMenuSubTrigger: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

vi.mock("@/components/ui/inline-edit", () => ({
  InlineEdit: ({ value }: { value: string }) => <span>{value}</span>,
}));

vi.mock("@/components/fleet/confirm-delete-session-dialog", () => ({
  ConfirmDeleteSessionDialog: ({ open, onConfirm }: { open: boolean; onConfirm: () => void }) =>
    open ? (
      <button type="button" data-testid="delete-dialog-confirm" onClick={onConfirm}>
        Confirm Delete
      </button>
    ) : null,
}));

vi.mock("@/components/session/fork-session-dialog", () => ({
  ForkSessionDialog: () => null,
}));

vi.mock("@/components/ui/open-tool-menu", () => ({
  OpenToolContextSubmenu: () => null,
}));

const item: SessionListItem = {
  instanceId: "inst-1",
  workspaceId: "ws-1",
  workspaceDirectory: "/tmp/project",
  workspaceDisplayName: null,
  isolationStrategy: "existing",
  sessionStatus: "active",
  session: {
    id: "session-1",
    title: "Test Session",
    time: { created: 0, updated: 0 },
  },
  instanceStatus: "running",
  parentSessionId: null,
  sourceDirectory: null,
  branch: null,
  activityStatus: "busy",
  lifecycleStatus: "running",
  retentionStatus: "active",
  archivedAt: null,
  typedInstanceStatus: "running",
  isHidden: false,
};

function renderWithProviders(isActive: boolean, refetch = vi.fn()) {
  const value: SessionsContextValue = {
    sessions: [item],
    retentionFilter: "active",
    setRetentionFilter: vi.fn(),
    isLoading: false,
    error: undefined,
    refetch: vi.fn(),
    summary: null,
    patchSessionTitle: vi.fn(),
    patchWorkspaceDisplayName: vi.fn(),
  };

  return {
    refetch,
    ...render(
      <SessionsContext.Provider value={value}>
        <MemoryRouter>
          <SidebarSessionItem item={item} isActive={isActive} refetch={refetch} userProjects={[]} />
        </MemoryRouter>
      </SessionsContext.Provider>,
    ),
  };
}

describe("SidebarSessionItem", () => {
  beforeEach(() => {
    mockNavigate.mockReset();
    mockDeleteSession.mockReset();
    mockDeleteSession.mockResolvedValue(undefined);
    mockArchiveSession.mockReset();
    mockArchiveSession.mockResolvedValue(undefined);
    mockUnarchiveSession.mockReset();
    mockUnarchiveSession.mockResolvedValue(undefined);
  });

  it("navigates to fleet after deleting the active session", async () => {
    const user = userEvent.setup();
    const { refetch } = renderWithProviders(true);

    await user.click(screen.getByRole("button", { name: "Permanently Delete" }));
    await user.click(screen.getByTestId("delete-dialog-confirm"));

    await waitFor(() => {
      expect(mockDeleteSession).toHaveBeenCalledWith("session-1", "inst-1");
      expect(refetch).toHaveBeenCalled();
      expect(mockNavigate).toHaveBeenCalledWith("/", { replace: true });
    });
  });

  it("does not navigate when deleting an inactive session", async () => {
    const user = userEvent.setup();
    const { refetch } = renderWithProviders(false);

    await user.click(screen.getByRole("button", { name: "Permanently Delete" }));
    await user.click(screen.getByTestId("delete-dialog-confirm"));

    await waitFor(() => {
      expect(mockDeleteSession).toHaveBeenCalledWith("session-1", "inst-1");
      expect(refetch).toHaveBeenCalled();
    });

    expect(mockNavigate).not.toHaveBeenCalled();
  });

  it("archives a stopped active session", async () => {
    const user = userEvent.setup();
    const stoppedItem = { ...item, lifecycleStatus: "stopped" as const, activityStatus: "idle" as const };
    const value: SessionsContextValue = {
      sessions: [stoppedItem],
      retentionFilter: "active",
      setRetentionFilter: vi.fn(),
      isLoading: false,
      error: undefined,
      refetch: vi.fn(),
      summary: null,
      patchSessionTitle: vi.fn(),
      patchWorkspaceDisplayName: vi.fn(),
    };

    render(
      <SessionsContext.Provider value={value}>
        <MemoryRouter>
          <SidebarSessionItem item={stoppedItem} isActive={false} refetch={value.refetch} userProjects={[]} />
        </MemoryRouter>
      </SessionsContext.Provider>,
    );

    await user.click(screen.getByRole("button", { name: "Archive" }));

    await waitFor(() => {
      expect(mockArchiveSession).toHaveBeenCalledWith("session-1");
    });
  });

  it("unarchives an archived session", async () => {
    const user = userEvent.setup();
    const archivedItem = { ...item, lifecycleStatus: "stopped" as const, activityStatus: "idle" as const, retentionStatus: "archived" as const, archivedAt: "2026-01-01T00:00:00Z" };
    const value: SessionsContextValue = {
      sessions: [archivedItem],
      retentionFilter: "archived",
      setRetentionFilter: vi.fn(),
      isLoading: false,
      error: undefined,
      refetch: vi.fn(),
      summary: null,
      patchSessionTitle: vi.fn(),
      patchWorkspaceDisplayName: vi.fn(),
    };

    render(
      <SessionsContext.Provider value={value}>
        <MemoryRouter>
          <SidebarSessionItem item={archivedItem} isActive={false} refetch={value.refetch} userProjects={[]} />
        </MemoryRouter>
      </SessionsContext.Provider>,
    );

    await user.click(screen.getByRole("button", { name: "Unarchive" }));

    await waitFor(() => {
      expect(mockUnarchiveSession).toHaveBeenCalledWith("session-1");
    });
  });
});
