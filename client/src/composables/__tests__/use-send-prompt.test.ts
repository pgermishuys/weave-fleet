import { afterEach, describe, expect, it, vi } from "vitest"
import { nextTick } from "vue"
import { useDraftState } from "@/composables/use-draft-state"
import {
  clearSentPrompts,
  confirmSentPrompt,
  reconcileSentPrompts,
  seedSentPrompt,
  useSendPrompt,
  useSentPrompts,
} from "@/composables/use-send-prompt"
import type { AccumulatedMessage } from "@/lib/client-types"

vi.mock("@/api/client", () => ({
  api: {
    GET: vi.fn(),
    POST: vi.fn(),
    PUT: vi.fn(),
    DELETE: vi.fn(),
    PATCH: vi.fn(),
  },
}))

vi.mock("@/composables/use-agents", async () => {
  const { computed, ref } = await import("vue")
  const agents = ref([{ id: "agent-1", name: "Loom", description: "" }])
  const agentsById = ref({ "agent-1": agents.value[0] })

  return {
    useAgents: () => ({
      agents,
      agentsById,
      defaultAgentId: computed(() => "agent-1"),
      isLoading: ref(false),
      error: ref(undefined),
      refresh: vi.fn(),
    }),
  }
})

vi.mock("@/composables/use-models", async () => {
  const { computed, ref } = await import("vue")
  const model = { id: "model-1", name: "Model", providerId: "provider-1", selectionKey: "model-key", provider: "Provider", description: "" }
  const models = ref([model])
  const modelsByKey = ref({ "model-key": model })

  return {
    useModels: () => ({
      models,
      modelsByKey,
      defaultModelKey: computed(() => "model-key"),
      isLoading: ref(false),
      error: ref(undefined),
      refresh: vi.fn(),
    }),
  }
})

import { api } from "@/api/client"

const mockApi = vi.mocked(api)

afterEach(() => {
  mockApi.POST.mockReset()
  vi.restoreAllMocks()
})

// A prompt request that never resolves, so the prompt stays unconfirmed until the test confirms it.
function sendUnconfirmedPrompt(sessionId: string, text: string, correlationSeed: string): void {
  mockApi.POST.mockReturnValueOnce(new Promise(() => {}))
  vi.spyOn(crypto, "randomUUID")
    .mockReturnValueOnce("optimistic-id" as `${string}-${string}-${string}-${string}-${string}`)
    .mockReturnValueOnce(correlationSeed as `${string}-${string}-${string}-${string}-${string}`)
  useDraftState(sessionId, { agentId: "", modelId: "" }).setText(text)
  useSendPrompt(sessionId).sendPrompt()
}

function deliveredUserMessage(sessionId: string, messageId: string, text: string): AccumulatedMessage {
  return {
    messageId,
    sessionId,
    role: "user",
    parts: [{ type: "text", text } as AccumulatedMessage["parts"][number]],
  }
}

describe("use-send-prompt pending prompts", () => {
  it("counts a sent prompt as pending until it is confirmed", async () => {
    const sessionId = "session-pending-confirm"
    const { hasPendingPrompts } = useSentPrompts(sessionId)

    sendUnconfirmedPrompt(sessionId, "Hello", "corr-confirm")
    await nextTick()
    expect(hasPendingPrompts.value).toBe(true)

    confirmSentPrompt(sessionId, { correlationId: "prompt-corrconfirm" })
    expect(hasPendingPrompts.value).toBe(false)
  })

  it("stops counting a prompt as pending when reconciliation removes it before confirmation", async () => {
    const sessionId = "session-pending-reconcile"
    const { hasPendingPrompts, sentPrompts } = useSentPrompts(sessionId)

    sendUnconfirmedPrompt(sessionId, "Hello", "corr-reconcile")
    await nextTick()

    // The delivered message carries the server's ID, so it matches by text.
    reconcileSentPrompts(sessionId, [deliveredUserMessage(sessionId, "user-server-id", "Hello")])
    expect(sentPrompts.value).toHaveLength(0)
    expect(hasPendingPrompts.value).toBe(false)

    // The late confirmation finds nothing to confirm and must not push the count below zero.
    confirmSentPrompt(sessionId, { correlationId: "prompt-corrreconcile", serverMessageId: "user-server-id" })
    expect(hasPendingPrompts.value).toBe(false)
  })

  it("stops counting a prompt as pending when sent prompts are cleared before confirmation", async () => {
    const sessionId = "session-pending-clear"
    const { hasPendingPrompts } = useSentPrompts(sessionId)

    sendUnconfirmedPrompt(sessionId, "Hello", "corr-clear")
    await nextTick()

    clearSentPrompts(sessionId)
    expect(hasPendingPrompts.value).toBe(false)
  })

  it("does not count a confirmed prompt twice when reconciliation removes it", async () => {
    const sessionId = "session-confirmed-reconcile"
    const { hasPendingPrompts } = useSentPrompts(sessionId)

    sendUnconfirmedPrompt(sessionId, "Hello", "corr-first")
    await nextTick()
    sendUnconfirmedPrompt(sessionId, "Second", "corr-second")
    await nextTick()

    confirmSentPrompt(sessionId, { correlationId: "prompt-corrfirst" })
    reconcileSentPrompts(sessionId, [deliveredUserMessage(sessionId, "user-server-id", "Hello")])

    // Only the second prompt is still waiting for its confirmation.
    expect(hasPendingPrompts.value).toBe(true)
    confirmSentPrompt(sessionId, { correlationId: "prompt-corrsecond" })
    expect(hasPendingPrompts.value).toBe(false)
  })
})

describe("seedSentPrompt (a new session's first message)", () => {
  it("shows the message as sent before the session's history has it", () => {
    const sessionId = "session-seeded-shown"
    const { sentPrompts, hasPendingPrompts } = useSentPrompts(sessionId)

    seedSentPrompt(sessionId, "  Fix the login redirect \n", 1_000)

    expect(sentPrompts.value).toHaveLength(1)
    expect(sentPrompts.value[0]).toMatchObject({ body: "Fix the login redirect", createdAt: 1_000, status: "pending" })
    expect(hasPendingPrompts.value).toBe(true)
  })

  it("gives way to the history's copy, so it never shows twice", () => {
    const sessionId = "session-seeded-reconciled"
    const { sentPrompts, hasPendingPrompts } = useSentPrompts(sessionId)
    seedSentPrompt(sessionId, "Fix the login redirect")

    reconcileSentPrompts(sessionId, [deliveredUserMessage(sessionId, "msg_server", "Fix the login redirect")])

    expect(sentPrompts.value).toHaveLength(0)
    expect(hasPendingPrompts.value).toBe(false)
  })

  it("waits while the history's copy has no text yet", () => {
    const sessionId = "session-seeded-textless"
    const { sentPrompts } = useSentPrompts(sessionId)
    seedSentPrompt(sessionId, "Fix the login redirect")

    reconcileSentPrompts(sessionId, [{ messageId: "msg_server", sessionId, role: "user", parts: [] }])

    expect(sentPrompts.value).toHaveLength(1)
  })

  it("gives way to a GitHub start's message, which the server wraps in the issue's context", () => {
    const sessionId = "session-seeded-github"
    const { sentPrompts } = useSentPrompts(sessionId)
    seedSentPrompt(sessionId, "Start with the tests")

    reconcileSentPrompts(sessionId, [
      deliveredUserMessage(sessionId, "msg_server", "[Source: acme/rocket#42]\n\nLogin loops\n\nStart with the tests"),
    ])

    expect(sentPrompts.value).toHaveLength(0)
  })

  it("seeds nothing for an empty message", () => {
    const sessionId = "session-seeded-empty"
    const { sentPrompts, hasPendingPrompts } = useSentPrompts(sessionId)

    seedSentPrompt(sessionId, "   ")

    expect(sentPrompts.value).toHaveLength(0)
    expect(hasPendingPrompts.value).toBe(false)
  })
})

describe("use-send-prompt retry", () => {
  it("re-sends the failed prompt without touching what is in the composer", async () => {
    const sessionId = "session-retry-draft"
    const draft = useDraftState(sessionId, { agentId: "", modelId: "" })
    draft.setText("something else I started typing")

    mockApi.POST.mockReturnValueOnce(new Promise(() => {}))
    const sent = useSendPrompt(sessionId).retryPrompt("the prompt that failed")
    await nextTick()

    expect(sent).toBe(true)
    expect(draft.draft.text).toBe("something else I started typing")
    expect(mockApi.POST).toHaveBeenCalledTimes(1)
    const promptCall = (mockApi.POST.mock.calls as unknown[][]).find(([url]) => url === "/api/sessions/{id}/prompt")
    expect((promptCall?.[1] as { body: { text: string } }).body.text).toBe("the prompt that failed")
  })

  it("clears the composer when the draft itself is sent", async () => {
    const sessionId = "session-retry-normal-send"
    const draft = useDraftState(sessionId, { agentId: "", modelId: "" })
    draft.setText("a normal prompt")

    mockApi.POST.mockReturnValueOnce(new Promise(() => {}))
    useSendPrompt(sessionId).sendPrompt()
    await nextTick()

    expect(draft.draft.text).toBe("")
  })
})

describe("use-send-prompt picks the harness no longer lists", () => {
  function sentBody(): { agent?: string; model?: unknown } {
    const promptCall = (mockApi.POST.mock.calls as unknown[][]).find(([url]) => url === "/api/sessions/{id}/prompt")
    return (promptCall?.[1] as { body: { agent?: string; model?: unknown } }).body
  }

  it("sends the agent and model as picked instead of swapping them for the first listed", async () => {
    const sessionId = "session-gone-picks"
    const draft = useDraftState(sessionId, { agentId: "", modelId: "" })
    draft.setAgentId("removed-agent")
    draft.setModelId(JSON.stringify(["signed-out", "gone-model"]))
    draft.setText("hello")

    mockApi.POST.mockReturnValueOnce(new Promise(() => {}))
    useSendPrompt(sessionId).sendPrompt()
    await nextTick()

    expect(sentBody().agent).toBe("removed-agent")
    expect(sentBody().model).toEqual({ providerID: "signed-out", modelID: "gone-model" })
    const [sent] = useSentPrompts(sessionId).sentPrompts.value
    expect(sent?.agentName).toBe("removed-agent")
    expect(sent?.modelName).toBe("gone-model")
  })

  it("sends neither when the draft is on Default", async () => {
    const sessionId = "session-default-picks"
    useDraftState(sessionId, { agentId: "", modelId: "" }).setText("hello")

    mockApi.POST.mockReturnValueOnce(new Promise(() => {}))
    useSendPrompt(sessionId).sendPrompt()
    await nextTick()

    expect(sentBody().agent).toBeUndefined()
    expect(sentBody().model).toBeUndefined()
  })
})

describe("use-send-prompt steering", () => {
  function sentBody(): { text: string; delivery?: string } {
    const promptCall = (mockApi.POST.mock.calls as unknown[][]).find(([url]) => url === "/api/sessions/{id}/prompt")
    return (promptCall?.[1] as { body: { text: string; delivery?: string } }).body
  }

  it("asks for the running turn and shows the message as sent mid-turn", async () => {
    const sessionId = "session-steer-draft"
    useDraftState(sessionId, { agentId: "", modelId: "" }).setText("stop, wrong file")

    mockApi.POST.mockReturnValueOnce(new Promise(() => {}))
    useSendPrompt(sessionId).sendPrompt(undefined, undefined, { steer: true })
    await nextTick()

    expect(sentBody()).toMatchObject({ text: "stop, wrong file", delivery: "steer" })
    expect(useSentPrompts(sessionId).sentPrompts.value[0]?.steered).toBe(true)
  })

  it("sends a queued message now without touching what is in the composer", async () => {
    const sessionId = "session-steer-queued"
    const draft = useDraftState(sessionId, { agentId: "", modelId: "" })
    draft.setText("half a thought")

    mockApi.POST.mockReturnValueOnce(new Promise(() => {}))
    useSendPrompt(sessionId).sendPrompt(undefined, "the queued message", { steer: true })
    await nextTick()

    expect(sentBody()).toMatchObject({ text: "the queued message", delivery: "steer" })
    expect(draft.draft.text).toBe("half a thought")
  })

  it("leaves the delivery out of a normal send, which the server queues", async () => {
    const sessionId = "session-steer-normal"
    useDraftState(sessionId, { agentId: "", modelId: "" }).setText("after the turn")

    mockApi.POST.mockReturnValueOnce(new Promise(() => {}))
    useSendPrompt(sessionId).sendPrompt()
    await nextTick()

    expect(sentBody().delivery).toBeUndefined()
    expect(useSentPrompts(sessionId).sentPrompts.value[0]?.steered).toBeUndefined()
  })
})
