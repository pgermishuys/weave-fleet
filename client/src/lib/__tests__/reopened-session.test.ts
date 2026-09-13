import { describe, expect, it } from "vitest"
import { applyDomainEvent, createSessionStreamState } from "@/lib/domain-event-reducer"
import { toToolCardItem } from "@/components/session/activity-stream-tool-card"
import type { AccumulatedToolPart } from "@/lib/client-types"
import type { MessageEventPart } from "@/lib/domain-events"
import type { SessionSnapshot } from "@/lib/session-snapshot"

// A session reopened after navigating away: the server rebuilds it from OpenCode's history, using OpenCode's own
// part and call ids, the same ones the live stream used.
function part(overrides: Record<string, unknown>): MessageEventPart {
  return { sessionID: "fleet-1", messageID: "msg_1", ...overrides } as unknown as MessageEventPart
}

const snapshot: SessionSnapshot = {
  session: { id: "fleet-1", title: "Session", status: "active" },
  messages: [
    {
      info: {
        id: "msg_1",
        role: "assistant",
        sessionID: "fleet-1",
        agent: "build",
        modelID: "m",
        parentID: null,
        time: { created: 1000, completed: null },
        cost: null,
        tokens: null,
      },
      parts: [
        part({
          type: "tool",
          id: "prt_task",
          tool: "task",
          callID: "toolu_task",
          state: {
            status: "completed",
            input: { subagent_type: "thread" },
            output: "The sub-agent's answer",
            title: "Map the code",
            metadata: { sessionId: "ses_child" },
          },
        }),
        part({
          type: "tool",
          id: "prt_vis",
          tool: "visualize",
          callID: "toolu_vis",
          state: {
            status: "completed",
            input: {},
            output: "{\"$type\":\"visual/flow\",\"content\":{\"nodes\":[]}}",
            title: "Flow",
            metadata: { truncated: false },
          },
        }),
        part({
          type: "tool",
          id: "prt_canvas",
          tool: "fleet_canvas_open",
          callID: "toolu_canvas",
          state: { status: "completed", input: {}, output: "Opened it.", title: "Open canvas", metadata: { canvasId: "cv_1" } },
        }),
        part({
          type: "tool",
          id: "prt_run",
          tool: "bash",
          callID: "toolu_run",
          state: { status: "running", input: { command: "sleep 5" } },
        }),
      ],
    },
  ],
  delegations: [
    {
      delegationId: "del-1",
      parentToolCallId: "toolu_task",
      childSessionId: "fleet-child",
      title: "thread",
      status: "completed",
      createdAt: "2026-09-13T00:00:00Z",
    },
  ],
  activityStatus: "busy",
  lastEventId: null,
  hasMore: false,
  cursor: null,
  isPartial: false,
}

function toolParts(state: ReturnType<typeof createSessionStreamState>): AccumulatedToolPart[] {
  return state.messages[0].parts.filter((p): p is AccumulatedToolPart => p.type === "tool")
}

describe("Reopened session", () => {
  it("links the sub-agent card to its delegation", () => {
    const state = createSessionStreamState(snapshot)
    const task = toolParts(state).find((p) => p.tool === "task")

    expect(state.delegations[0].parentToolCallId).toBe(task?.callId)
  })

  it("keeps the visual card and the canvas card", () => {
    const tools = toolParts(createSessionStreamState(snapshot)).map(toToolCardItem)

    expect(tools.find((t) => t.kind === "visualize")?.output).toContain("visual/flow")
    expect(tools.find((t) => t.kind === "fleet_canvas_open")?.canvasId).toBe("cv_1")
  })

  it("updates a tool still running when reopened instead of adding a second card", () => {
    const reopened = createSessionStreamState(snapshot)

    const next = applyDomainEvent(reopened, {
      type: "message.part.updated",
      payload: {
        sessionID: "fleet-1",
        part: part({
          type: "tool",
          id: "prt_run",
          tool: "bash",
          callID: "toolu_run",
          state: { status: "completed", input: { command: "sleep 5" }, output: "done", metadata: null },
        }),
      },
    })

    const bash = toolParts(next).filter((p) => p.tool === "bash")
    expect(bash).toHaveLength(1)
    expect((bash[0].state as Record<string, unknown>).status).toBe("completed")
  })
})
