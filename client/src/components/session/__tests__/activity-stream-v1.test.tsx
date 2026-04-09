import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { describe, expect, it, vi } from "vitest";
import { ActivityStreamV1 } from "@/components/session/activity-stream-v1";
import type { AccumulatedMessage, DelegationDto } from "@/lib/api-types";
import { SessionsContext } from "@/contexts/sessions-context";

vi.mock("@/hooks/use-scroll-anchor", () => ({
  useScrollAnchor: () => ({
    scrollRef: { current: null },
    isAtBottom: true,
    isNearTop: false,
    newMessageCount: 0,
    scrollToBottom: vi.fn(),
    preserveScrollPosition: (fn: () => void) => fn(),
    getScrollPosition: () => null,
    restoreScrollPosition: vi.fn(),
    suppressAutoScroll: { current: false },
    viewportElement: null,
  }),
}));

vi.mock("@tanstack/react-virtual", () => ({
  useVirtualizer: ({ count }: { count: number }) => ({
    getTotalSize: () => count * 120,
    getVirtualItems: () => Array.from({ length: count }, (_, index) => ({ index, start: index * 120 })),
    measureElement: vi.fn(),
  }),
}));

function renderActivityStream(messages: AccumulatedMessage[], delegations: DelegationDto[]) {
  return render(
    <SessionsContext.Provider
      value={{
        sessions: [
          {
            instanceId: "child-inst-1",
            workspaceId: "ws-1",
            workspaceDirectory: "/tmp",
            workspaceDisplayName: null,
            isolationStrategy: "existing",
            sessionStatus: "active",
            session: { id: "child-1", title: "Child", time: { created: 0, updated: 0 } },
            instanceStatus: "running",
            parentSessionId: "parent",
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
        retentionFilter: "active",
        setRetentionFilter: vi.fn(),
        isLoading: false,
        error: undefined,
        refetch: vi.fn(),
        summary: null,
        patchSessionTitle: vi.fn(),
        patchWorkspaceDisplayName: vi.fn(),
      }}
    >
      <MemoryRouter initialEntries={["/sessions/parent?instanceId=inst-1"]}>
        <ActivityStreamV1
          messages={messages}
          delegations={delegations}
          status="connected"
          sessionStatus="idle"
          currentSessionId="parent"
        />
      </MemoryRouter>
    </SessionsContext.Provider>,
  );
}

describe("ActivityStreamV1 delegations", () => {
  it("renders DelegationCard in place of matching tool call", () => {
    const messages: AccumulatedMessage[] = [
      {
        messageId: "msg-1",
        sessionId: "parent",
        role: "assistant",
        parts: [
          {
            partId: "part-1",
            type: "tool",
            tool: "task",
            callId: "call-1",
            state: {
              input: { subagent_type: "thread", description: "legacy description" },
              status: "running",
            },
          },
        ],
      },
    ];

    const delegations: DelegationDto[] = [
      {
        delegationId: "del-1",
        parentToolCallId: "call-1",
        childSessionId: "child-1",
        title: "Reviewer Task",
        status: "running",
      },
    ];

    renderActivityStream(messages, delegations);

    expect(screen.getByText("Reviewer Task")).toBeTruthy();
    expect(screen.queryByText("legacy description")).toBeNull();
  });

  it("renders unanchored delegations after the message stream", () => {
    const messages: AccumulatedMessage[] = [
      {
        messageId: "msg-1",
        sessionId: "parent",
        role: "assistant",
        parts: [
          {
            partId: "part-1",
            type: "text",
            text: "hello",
          },
        ],
      },
    ];

    const delegations: DelegationDto[] = [
      {
        delegationId: "del-2",
        parentToolCallId: "missing-call",
        childSessionId: null,
        title: "Code Review",
        status: "pending",
      },
    ];

    renderActivityStream(messages, delegations);

    expect(screen.getByText("Delegations")).toBeTruthy();
    expect(screen.getByText("Code Review")).toBeTruthy();
  });
});
