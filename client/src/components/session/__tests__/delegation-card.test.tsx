/**
 * @vitest-environment jsdom
 */

import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { describe, expect, it } from "vitest";
import { DelegationCard } from "@/components/session/delegation-card";
import { SessionsContext, type SessionsContextValue } from "@/contexts/sessions-context";

function renderWithSessions(ui: React.ReactNode, sessions: SessionsContextValue["sessions"]) {
  return render(
    <SessionsContext.Provider
      value={{
        sessions,
        retentionFilter: "active",
        setRetentionFilter: () => {},
        isLoading: false,
        error: undefined,
        refetch: () => {},
        summary: null,
        patchSessionTitle: () => {},
        patchWorkspaceDisplayName: () => {},
      }}
    >
      <MemoryRouter>{ui}</MemoryRouter>
    </SessionsContext.Provider>,
  );
}

describe("DelegationCard", () => {
  it("renders status and title", () => {
    renderWithSessions(
      <DelegationCard
        delegation={{
          delegationId: "del-1",
          parentToolCallId: "tool-1",
          childSessionId: null,
          title: "reviewer",
          status: "pending",
        }}
      />,
      [],
    );

    expect(screen.getByText("reviewer")).toBeTruthy();
    expect(screen.getByText("pending")).toBeTruthy();
  });

  it("renders child session link when child session exists", () => {
    renderWithSessions(
      <DelegationCard
        delegation={{
          delegationId: "del-1",
          parentToolCallId: "tool-1",
          childSessionId: "child-1",
          title: "reviewer",
          status: "running",
        }}
        currentSessionId="parent-1"
      />,
      [
        {
          instanceId: "child-inst-1",
          workspaceId: "ws-1",
          workspaceDirectory: "/tmp",
          workspaceDisplayName: null,
          isolationStrategy: "existing",
          sessionStatus: "active",
          session: { id: "child-1", title: "Child", time: { created: 0, updated: 0 } },
          instanceStatus: "running",
          parentSessionId: "parent-1",
          sourceDirectory: null,
          branch: null,
           activityStatus: "busy",
           lifecycleStatus: "running",
           retentionStatus: "active",
           archivedAt: null,
           typedInstanceStatus: "running",
           isHidden: true,
        },
      ],
    );

    const link = screen.getByRole("link");
    expect(link.getAttribute("href")).toContain("/sessions/child-1");
    expect(link.getAttribute("href")).toContain("instanceId=child-inst-1");
  });

  it("includes parent session context in child link", () => {
    renderWithSessions(
      <DelegationCard
        delegation={{
          delegationId: "del-1",
          parentToolCallId: "tool-1",
          childSessionId: "child-1",
          title: "reviewer",
          status: "running",
        }}
        currentSessionId="parent-1"
      />,
      [
        {
          instanceId: "child-inst-1",
          workspaceId: "ws-1",
          workspaceDirectory: "/tmp",
          workspaceDisplayName: null,
          isolationStrategy: "existing",
          sessionStatus: "active",
          session: { id: "child-1", title: "Child", time: { created: 0, updated: 0 } },
          instanceStatus: "running",
          parentSessionId: "parent-1",
          sourceDirectory: null,
          branch: null,
           activityStatus: "busy",
           lifecycleStatus: "running",
           retentionStatus: "active",
           archivedAt: null,
           typedInstanceStatus: "running",
           isHidden: true,
        },
      ],
    );

    const link = screen.getByRole("link");
    expect(link.getAttribute("href")).toContain("parentSessionId=parent-1");
  });
});
