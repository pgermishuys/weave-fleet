import { describe, expect, it } from "vitest"
import { applyPartUpdate } from "@/lib/event-state"
import type { AccumulatedMessage } from "@/lib/client-types"

const tool = (messageID: string, id: string, callID: string, state: Record<string, unknown>) => ({
  id, sessionID: "s1", messageID, type: "tool", tool: "bash", callID, state,
})

describe("late tool update after the next turn started", () => {
  const base: AccumulatedMessage[] = [
    { messageId: "msg_001", sessionId: "s1", role: "assistant", parts: [] },
    { messageId: "msg_002", sessionId: "s1", role: "user", parts: [] },
    { messageId: "msg_003", sessionId: "s1", role: "assistant", parts: [] },
  ]

  it("updates the existing part of the older message, not appending", () => {
    let m = applyPartUpdate(base, tool("msg_001", "prt_1", "call_1", { status: "running" }))
    m = applyPartUpdate(m, tool("msg_003", "prt_9", "call_9", { status: "running" }))
    m = applyPartUpdate(m, tool("msg_001", "prt_1", "call_1", { status: "completed", output: "ok" }))
    const parts = m.flatMap((x) => x.parts.filter((p) => p.type === "tool"))
    expect(parts).toHaveLength(2)
    expect(m[0].parts).toHaveLength(1)
    expect((m[0].parts[0] as { state: { status: string } }).state.status).toBe("completed")
  })

  it("same call id arriving under a different part id does not duplicate", () => {
    let m = applyPartUpdate(base, tool("msg_001", "prt_1", "call_1", { status: "running" }))
    m = applyPartUpdate(m, tool("msg_001", "prt_1b", "call_1", { status: "completed" }))
    expect(m[0].parts.filter((p) => p.type === "tool")).toHaveLength(1)
  })
})
