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
import { useModelPickerStore } from "@/stores/model-picker"
import { useSessionsStore } from "@/stores/sessions"
import { rememberSessionSkills } from "@/lib/skill-mentions"
import { rememberSessionReference } from "@/lib/session-references"

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

describe("use-send-prompt refused sends", () => {
  const refused = () => ({ error: { error: "This session's folder isn't there any more." }, response: new Response(null, { status: 409 }) })
  const settle = () => new Promise((resolve) => setTimeout(resolve, 0))

  it("clears the composer at once and puts what was typed back when the server refuses it", async () => {
    const sessionId = "session-refused-text"
    const draft = useDraftState(sessionId, { agentId: "", modelId: "" })
    draft.setText("  fix the flaky login test\n")
    let answer: (value: unknown) => void = () => {}
    mockApi.POST.mockReturnValueOnce(new Promise((resolve) => { answer = resolve }))

    useSendPrompt(sessionId).sendPrompt()
    expect(draft.draft.text).toBe("")

    answer(refused())
    await settle()
    expect(draft.draft.text).toBe("  fix the flaky login test\n")
  })

  it("puts what was typed back when the request never reaches the server", async () => {
    const sessionId = "session-refused-network"
    const draft = useDraftState(sessionId, { agentId: "", modelId: "" })
    draft.setText("hello")
    mockApi.POST.mockRejectedValueOnce(new TypeError("Failed to fetch"))

    useSendPrompt(sessionId).sendPrompt()
    await settle()

    expect(draft.draft.text).toBe("hello")
  })

  it("leaves what was typed while it was sending alone, and still says what came back", async () => {
    const sessionId = "session-refused-typed-meanwhile"
    const draft = useDraftState(sessionId, { agentId: "", modelId: "" })
    draft.setText("first message")
    let answer: (value: unknown) => void = () => {}
    mockApi.POST.mockReturnValueOnce(new Promise((resolve) => { answer = resolve }))
    const onRefused = vi.fn()

    useSendPrompt(sessionId).sendPrompt(undefined, undefined, { onRefused })
    draft.setText("something new")
    answer(refused())
    await settle()

    expect(draft.draft.text).toBe("something new")
    expect(onRefused).toHaveBeenCalledTimes(1)
  })

  it("tells the caller a send was refused, so it can put back what it cleared, and not one that was taken", async () => {
    const sessionId = "session-refused-callback"
    const draft = useDraftState(sessionId, { agentId: "", modelId: "" })
    const onRefused = vi.fn()

    draft.setText("taken")
    mockApi.POST.mockResolvedValueOnce({ data: { correlationId: "c" }, response: new Response(null, { status: 202 }) })
    useSendPrompt(sessionId).sendPrompt(undefined, undefined, { onRefused })
    await settle()
    expect(onRefused).not.toHaveBeenCalled()

    draft.setText("refused")
    mockApi.POST.mockResolvedValueOnce(refused())
    useSendPrompt(sessionId).sendPrompt(undefined, undefined, { onRefused })
    await settle()
    expect(onRefused).toHaveBeenCalledTimes(1)
  })

  it("leaves the composer alone when a re-sent prompt is refused", async () => {
    const sessionId = "session-refused-retry"
    const draft = useDraftState(sessionId, { agentId: "", modelId: "" })
    mockApi.POST.mockResolvedValueOnce(refused())

    useSendPrompt(sessionId).retryPrompt("the prompt that failed")
    await settle()

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

  it("records a picked model the server took as recent for the session's harness, and Default or a refusal as nothing", async () => {
    const sessionId = "session-recent-pick"
    vi.spyOn(useSessionsStore(), "sessionById").mockReturnValue({ harnessType: "opencode", lifecycleStatus: "running" } as never)
    const record = vi.spyOn(useModelPickerStore(), "recordRecent").mockResolvedValue()
    const draft = useDraftState(sessionId, { agentId: "", modelId: "" })
    const taken = { data: { correlationId: "c" }, response: { ok: true, status: 200 } }
    const send = async (text: string) => {
      draft.setText(text)
      useSendPrompt(sessionId).sendPrompt()
      await new Promise((resolve) => setTimeout(resolve, 0))
    }

    mockApi.POST.mockResolvedValueOnce(taken)
    await send("hello")
    expect(record).not.toHaveBeenCalled()

    draft.setModelId(JSON.stringify(["p", "m"]))
    mockApi.POST.mockResolvedValueOnce({ error: { error: "Unknown model" }, response: new Response("{}", { status: 400 }) })
    await send("refused")
    expect(record).not.toHaveBeenCalled()

    mockApi.POST.mockResolvedValueOnce(taken)
    await send("again")
    expect(record).toHaveBeenCalledWith("opencode:p/m")
  })

  it("says why the server refused a prompt, from the error the API client already read", async () => {
    const sessionId = "session-refused"
    vi.spyOn(useSessionsStore(), "sessionById").mockReturnValue({ harnessType: "claude-code", lifecycleStatus: "running" } as never)
    const message = "This session's folder isn't there any more: /work/harbor-api. The conversation is kept."
    // The API client reads the body to give the error, so the response's own body is used up by then.
    const response = new Response(JSON.stringify({ error: message }), { status: 409 })
    await response.text()
    mockApi.POST.mockResolvedValueOnce({ error: { error: message }, response })
    useDraftState(sessionId, { agentId: "", modelId: "" }).setText("hello")
    const send = useSendPrompt(sessionId)

    send.sendPrompt()
    await new Promise((resolve) => setTimeout(resolve, 0))

    expect(send.error.value).toBe(message)
  })

  it("puts a session the server refused a prompt for back as it was, instead of leaving it working", async () => {
    const sessionId = "session-refused-status"
    const session = { harnessType: "claude-code", activityStatus: "idle", lifecycleStatus: "stopped", sessionStatus: "stopped", capabilities: { canPrompt: true } }
    vi.spyOn(useSessionsStore(), "sessionById").mockReturnValue(session as never)
    mockApi.POST.mockResolvedValueOnce({ error: { error: "This session's folder isn't there any more." }, response: new Response(null, { status: 409 }) })
    useDraftState(sessionId, { agentId: "", modelId: "" }).setText("hello")

    useSendPrompt(sessionId).sendPrompt()
    expect(session.activityStatus).toBe("busy")
    await new Promise((resolve) => setTimeout(resolve, 0))

    expect(session).toMatchObject({ activityStatus: "idle", lifecycleStatus: "stopped", sessionStatus: "stopped" })
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

describe("use-send-prompt $ skills", () => {
  it("sends the session's skills the text names, in order, and leaves the text as typed", async () => {
    const sessionId = "session-skills"
    rememberSessionSkills(sessionId, [{ name: "fleet-debug", kind: "skill" }, { name: "fleet-simplify", kind: "skill" }])
    useDraftState(sessionId, { agentId: "", modelId: "" }).setText("Tidy with $fleet-simplify after $fleet-debug, not $HOME")

    mockApi.POST.mockReturnValueOnce(new Promise(() => {}))
    useSendPrompt(sessionId).sendPrompt()
    await nextTick()

    const promptCall = (mockApi.POST.mock.calls as unknown[][]).find(([url]) => url === "/api/sessions/{id}/prompt")
    expect((promptCall?.[1] as { body: unknown }).body).toMatchObject({
      text: "Tidy with $fleet-simplify after $fleet-debug, not $HOME",
      skills: ["fleet-simplify", "fleet-debug"],
    })
  })
})

describe("use-send-prompt @ sessions", () => {
  function sentBody(): { text: string; sessionReferences?: { token: string; sessionId: string }[] } {
    const promptCall = (mockApi.POST.mock.calls as unknown[][]).find(([url]) => url === "/api/sessions/{id}/prompt")
    return (promptCall?.[1] as { body: { text: string; sessionReferences?: { token: string; sessionId: string }[] } }).body
  }

  it("sends the sessions picked from the @ list whose tokens are still in the text, and shows them on the sent message", async () => {
    const sessionId = "session-at-sessions"
    rememberSessionReference(sessionId, { token: "@t3code-notes", sessionId: "ses-a", title: "t3code notes" })
    rememberSessionReference(sessionId, { token: "@removed", sessionId: "ses-b", title: "Removed" })
    useDraftState(sessionId, { agentId: "", modelId: "" }).setText("Use the mapping from @t3code-notes")

    mockApi.POST.mockReturnValueOnce(new Promise(() => {}))
    useSendPrompt(sessionId).sendPrompt()
    await nextTick()

    expect(sentBody()).toMatchObject({
      text: "Use the mapping from @t3code-notes",
      sessionReferences: [{ token: "@t3code-notes", sessionId: "ses-a" }],
    })
    expect(useSentPrompts(sessionId).sentPrompts.value[0]?.sessionReferences).toEqual([
      { token: "@t3code-notes", sessionId: "ses-a", title: "t3code notes" },
    ])
  })

  it("sends no references for a message without any", async () => {
    const sessionId = "session-at-sessions-none"
    useDraftState(sessionId, { agentId: "", modelId: "" }).setText("Just a message")

    mockApi.POST.mockReturnValueOnce(new Promise(() => {}))
    useSendPrompt(sessionId).sendPrompt()
    await nextTick()

    expect(sentBody().sessionReferences).toBeUndefined()
  })

  it("gives way to the history's copy, which carries Fleet's block for the agent after the text", () => {
    const sessionId = "session-at-sessions-reconciled"
    const { sentPrompts } = useSentPrompts(sessionId)
    seedSentPrompt(sessionId, "Use the mapping from @t3code-notes")

    reconcileSentPrompts(sessionId, [
      deliveredUserMessage(
        sessionId,
        "msg_server",
        "Use the mapping from @t3code-notes\n\n<fleet-session-references>\nNote\n<session ref=\"@t3code-notes\" id=\"ses-a\" title=\"t3code notes\" />\n</fleet-session-references>",
      ),
    ])

    expect(sentPrompts.value).toHaveLength(0)
  })
})
