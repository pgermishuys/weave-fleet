import { storeToRefs } from "pinia";
import { computed, reactive, readonly, shallowRef } from "vue";
import { useAgents } from "@/composables/use-agents";
import { useDraftState, type EffortLevel } from "@/composables/use-draft-state";
import { useModels } from "@/composables/use-models";
import { apiFetch } from "@/lib/api-client";
import type { AccumulatedMessage, ImageAttachment } from "@/lib/api-types";
import { diagLog } from "@/lib/message-diagnostics";
import { useSessionsStore } from "@/stores/sessions";

export interface SentPromptImage {
  url: string;
  filename: string;
}

export interface SentPromptMessage {
  id: string;
  correlationId: string;
  eventId?: number;
  serverMessageId?: string;
  status: "pending" | "confirmed" | "needs_retry";
  body: string;
  createdAt: number;
  agentId: string;
  agentName: string;
  modelId: string;
  modelName: string;
  effort: EffortLevel;
  images: SentPromptImage[];
}

const sentPromptRegistry = reactive<Record<string, SentPromptMessage[]>>({});
const pendingPromptRegistry = reactive<Record<string, number>>({});
const promptConfirmationTimers = new Map<string, ReturnType<typeof setTimeout>>();
const PROMPT_CONFIRMATION_TIMEOUT_MS = 15_000;

function buildPromptTimerKey(sessionId: string, correlationId: string): string {
  return `${sessionId}:${correlationId}`;
}

function ensureSentPrompts(sessionId: string): SentPromptMessage[] {
  const existingPrompts = sentPromptRegistry[sessionId];
  if (existingPrompts) {
    return existingPrompts;
  }

  const prompts: SentPromptMessage[] = [];
  sentPromptRegistry[sessionId] = prompts;
  return prompts;
}

function ensurePendingPromptCount(sessionId: string): number {
  const existingCount = pendingPromptRegistry[sessionId];
  if (typeof existingCount === "number") {
    return existingCount;
  }

  pendingPromptRegistry[sessionId] = 0;
  return 0;
}

function removeSentPromptByCorrelationId(sessionId: string, correlationId: string): void {
  const prompts = sentPromptRegistry[sessionId];
  if (!prompts) {
    return;
  }

  const promptIndex = prompts.findIndex((prompt) => prompt.correlationId === correlationId);
  if (promptIndex >= 0) {
    clearPromptConfirmationTimeout(sessionId, correlationId);
    prompts.splice(promptIndex, 1);
  }
}

function clearPromptConfirmationTimeout(sessionId: string, correlationId: string): void {
  const timerKey = buildPromptTimerKey(sessionId, correlationId);
  const timer = promptConfirmationTimers.get(timerKey);
  if (!timer) {
    return;
  }

  clearTimeout(timer);
  promptConfirmationTimers.delete(timerKey);
}

function schedulePromptConfirmationTimeout(sessionId: string, correlationId: string): void {
  clearPromptConfirmationTimeout(sessionId, correlationId);

  const timerKey = buildPromptTimerKey(sessionId, correlationId);
  promptConfirmationTimers.set(timerKey, setTimeout(() => {
    promptConfirmationTimers.delete(timerKey);

    const prompts = sentPromptRegistry[sessionId];
    const prompt = prompts?.find((item) => item.correlationId === correlationId);
    if (!prompt || prompt.status === "confirmed") {
      return;
    }

    if (prompt.status === "pending") {
      decrementPendingPrompts(sessionId);
    }

    prompt.status = "needs_retry";
  }, PROMPT_CONFIRMATION_TIMEOUT_MS));
}

function clearPromptConfirmationTimeouts(sessionId: string): void {
  for (const prompt of sentPromptRegistry[sessionId] ?? []) {
    clearPromptConfirmationTimeout(sessionId, prompt.correlationId);
  }
}

export function clearSentPrompts(sessionId: string): void {
  const existing = sentPromptRegistry[sessionId];
  if (existing && existing.length > 0) {
    diagLog("prompt.clear", `clearing ${existing.length} optimistic prompt(s)`, {
      sessionId,
      prompts: existing.map((p) => ({ id: p.id, bodySnippet: p.body.slice(0, 60) })),
    });
  }
  clearPromptConfirmationTimeouts(sessionId);
  delete sentPromptRegistry[sessionId];
}

export function incrementPendingPrompts(sessionId: string): void {
  pendingPromptRegistry[sessionId] = (pendingPromptRegistry[sessionId] ?? 0) + 1;
}

export function decrementPendingPrompts(sessionId: string): void {
  const nextCount = (pendingPromptRegistry[sessionId] ?? 0) - 1;
  if (nextCount > 0) {
    pendingPromptRegistry[sessionId] = nextCount;
    return;
  }

  delete pendingPromptRegistry[sessionId];
}

export function clearPendingPrompts(sessionId: string): void {
  delete pendingPromptRegistry[sessionId];
}

function buildDeliveredPromptCounts(messages: readonly AccumulatedMessage[]): Map<string, number> {
  const deliveredPromptCounts = new Map<string, number>();

  for (const message of messages) {
    if (message.role !== "user") {
      continue;
    }

    const text = message.parts
      .filter((part): part is Extract<AccumulatedMessage["parts"][number], { type: "text" }> => part.type === "text")
      .map((part) => part.text)
      .join("\n\n")
      .trim();

    if (!text) {
      continue;
    }

    deliveredPromptCounts.set(text, (deliveredPromptCounts.get(text) ?? 0) + 1);
  }

  return deliveredPromptCounts;
}

export function reconcileSentPrompts(sessionId: string, messages: readonly AccumulatedMessage[]): void {
  const prompts = sentPromptRegistry[sessionId];
  if (!prompts || prompts.length === 0) {
    return;
  }

  const deliveredPromptIds = new Set(
    messages
      .filter((message) => message.role === "user")
      .map((message) => message.messageId),
  );

  const remainingAfterIdMatch = prompts.filter((prompt) => !deliveredPromptIds.has(prompt.id));
  if (remainingAfterIdMatch.length !== prompts.length) {
    for (const prompt of prompts.filter((prompt) => deliveredPromptIds.has(prompt.id))) {
      clearPromptConfirmationTimeout(sessionId, prompt.correlationId);
    }

    diagLog("prompt.reconcile", `removed ${prompts.length - remainingAfterIdMatch.length} optimistic prompt(s) by id`, {
      sessionId,
      removedPromptIds: prompts
        .filter((prompt) => deliveredPromptIds.has(prompt.id))
        .map((prompt) => prompt.id),
    });

    if (remainingAfterIdMatch.length === 0) {
      delete sentPromptRegistry[sessionId];
      return;
    }

    sentPromptRegistry[sessionId] = remainingAfterIdMatch;
    return;
  }

  const deliveredPromptCounts = buildDeliveredPromptCounts(messages);
  let unmatchedDeliveredPromptCount = 0;

  for (const message of messages) {
    if (message.role !== "user") {
      continue;
    }

    // Only count user messages that have actual text content as "delivered".
    // Messages that arrived via message.updated but haven't received their
    // message.part.updated yet have empty parts and must NOT be counted —
    // otherwise the optimistic prompt is removed before the delivered
    // message has text, causing a blank bubble.
    const hasText = message.parts.some(
      (part) => part.type === "text" && part.text.trim().length > 0,
    );
    if (hasText) {
      unmatchedDeliveredPromptCount += 1;
    }
  }

  const remainingPrompts = prompts.filter((prompt) => {
    const deliveredCount = deliveredPromptCounts.get(prompt.body) ?? 0;
    if (deliveredCount > 0) {
      deliveredPromptCounts.set(prompt.body, deliveredCount - 1);
      unmatchedDeliveredPromptCount -= 1;
      return false;
    }

    if (unmatchedDeliveredPromptCount > 0) {
      unmatchedDeliveredPromptCount -= 1;
      return false;
    }

    return true;
  });

  if (remainingPrompts.length < prompts.length) {
    for (const prompt of prompts.filter((prompt) => !remainingPrompts.includes(prompt))) {
      clearPromptConfirmationTimeout(sessionId, prompt.correlationId);
    }

    diagLog("prompt.reconcile", `removed ${prompts.length - remainingPrompts.length} of ${prompts.length} optimistic prompt(s)`, {
      sessionId,
      totalUserMessages: messages.filter((m) => m.role === "user").length,
      userMessagesWithText: messages.filter((m) => m.role === "user" && m.parts.some((p) => p.type === "text" && p.text.trim().length > 0)).length,
      remaining: remainingPrompts.length,
    });
  }

  if (remainingPrompts.length === prompts.length) {
    return;
  }

  if (remainingPrompts.length === 0) {
    delete sentPromptRegistry[sessionId];
    return;
  }

  sentPromptRegistry[sessionId] = remainingPrompts;
}

export interface ConfirmSentPromptOptions {
  correlationId?: string;
  eventId?: number | null;
  serverMessageId?: string;
}

export function confirmSentPrompt(sessionId: string, options: ConfirmSentPromptOptions): void {
  const prompts = sentPromptRegistry[sessionId];
  if (!prompts || prompts.length === 0) {
    return;
  }

  const prompt = prompts.find((candidate) => {
    if (options.correlationId && candidate.correlationId === options.correlationId) {
      return true;
    }

    return Boolean(options.serverMessageId && candidate.id === options.serverMessageId);
  });

  if (!prompt) {
    return;
  }

  if (prompt.status !== "confirmed") {
    decrementPendingPrompts(sessionId);
  }

  if (options.correlationId && prompt.correlationId !== options.correlationId) {
    clearPromptConfirmationTimeout(sessionId, prompt.correlationId);
    prompt.correlationId = options.correlationId;
  }

  clearPromptConfirmationTimeout(sessionId, prompt.correlationId);
  prompt.status = "confirmed";

  if (typeof options.eventId === "number") {
    prompt.eventId = options.eventId;
  }

  if (options.serverMessageId) {
    prompt.serverMessageId = options.serverMessageId;
    prompt.id = options.serverMessageId;
  }
}

export function useSentPrompts(sessionId: string) {
  ensureSentPrompts(sessionId);
  ensurePendingPromptCount(sessionId);

  const sentPrompts = computed(() => [...(sentPromptRegistry[sessionId] ?? [])]);
  const hasPendingPrompts = computed(() => (pendingPromptRegistry[sessionId] ?? 0) > 0);

  return {
    sentPrompts,
    hasPendingPrompts,
  };
}

interface BackendSendPromptRequest {
  text: string;
  agent?: string;
  model?: { providerID: string; modelID: string };
  attachments?: ImageAttachment[];
  userMessageId?: string;
  correlationId: string;
}

interface BackendSendPromptResponse {
  eventId?: number | null;
  correlationId?: string;
}

async function readPromptErrorMessage(response: Response): Promise<string> {
  const bodyText = await response.text().catch(() => "");
  if (!bodyText) {
    return `HTTP ${response.status}`;
  }

  try {
    const body = JSON.parse(bodyText) as Record<string, unknown>;

    if (typeof body.error === "string" && body.error.trim().length > 0) {
      return body.error;
    }

    if (typeof body.detail === "string" && body.detail.trim().length > 0) {
      return body.detail;
    }

    if (typeof body.title === "string" && body.title.trim().length > 0) {
      return body.title;
    }
  } catch {
    if (bodyText.trim().length > 0) {
      return bodyText.trim();
    }
  }

  return `HTTP ${response.status}`;
}

export function useSendPrompt(sessionId: string) {
  const sessionsStore = useSessionsStore();
  const { sessions } = storeToRefs(sessionsStore);
  const { defaultAgentId, agentsById } = useAgents();
  const { defaultModelKey, modelsByKey } = useModels(sessionId);
  const sendError = shallowRef<string | undefined>(undefined);
  const { draft, resetText } = useDraftState(sessionId, {
    agentId: "",
    modelId: "",
  });

  const selectedSession = computed(() => {
    return sessions.value.find((session) => session.session.id === sessionId) ?? null;
  });

  const sentPrompts = computed(() => sentPromptRegistry[sessionId] ?? []);
  const canSend = computed(() => draft.text.trim().length > 0);

  async function postPrompt(promptId: string, request: BackendSendPromptRequest): Promise<void> {
    try {
      const response = await apiFetch(`/api/sessions/${encodeURIComponent(sessionId)}/prompt`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify(request),
      });

      if (!response.ok) {
        throw new Error(await readPromptErrorMessage(response));
      }

      const receipt = await response.json().catch((): BackendSendPromptResponse => ({})) as BackendSendPromptResponse;
      confirmSentPrompt(sessionId, {
        correlationId: receipt.correlationId ?? request.correlationId,
        eventId: receipt.eventId,
      });

      sendError.value = undefined;
    } catch (caughtError) {
      removeSentPromptByCorrelationId(sessionId, request.correlationId);
      decrementPendingPrompts(sessionId);
      const message = caughtError instanceof Error ? caughtError.message : "Failed to send prompt";
      sendError.value = message;
      console.error("Failed to send prompt", caughtError);
    }
  }

  function sendPrompt(attachments?: ImageAttachment[]): boolean {
    const body = draft.text.trim();
    if (!body && (!attachments || attachments.length === 0)) {
      return false;
    }

    sendError.value = undefined;

    const agent = agentsById.value[draft.agentId] ?? agentsById.value[defaultAgentId.value];
    const model = modelsByKey.value[draft.modelId] ?? modelsByKey.value[defaultModelKey.value];
    const now = new Date();
    const promptId = `user-${crypto.randomUUID().replaceAll("-", "")}`;
    const correlationId = `prompt-${crypto.randomUUID().replaceAll("-", "")}`;
    const resolvedAgentId = agent?.id ?? draft.agentId ?? defaultAgentId.value;
    const resolvedModelId = model?.id ?? "";
    const usesDefaultAgent = !draft.agentId;
    const usesDefaultModel = !draft.modelId;

    ensureSentPrompts(sessionId).push({
      id: promptId,
      correlationId,
      status: "pending",
      body,
      createdAt: now.getTime(),
      agentId: resolvedAgentId,
      agentName: agent?.name ?? "Unknown agent",
      modelId: resolvedModelId,
      modelName: model?.name ?? "Unknown model",
      effort: draft.effort,
      images: attachments
        ? attachments.map((a) => ({
            url: `data:${a.mime};base64,${a.data}`,
            filename: a.filename ?? "image.png",
          }))
        : [],
    });
    incrementPendingPrompts(sessionId);
    schedulePromptConfirmationTimeout(sessionId, correlationId);

    if (selectedSession.value) {
      selectedSession.value.activityStatus = "busy";
      selectedSession.value.lifecycleStatus = "running";
      selectedSession.value.sessionStatus = "active";
    }

    resetText();
    const request: BackendSendPromptRequest = {
      text: body,
      userMessageId: promptId,
      correlationId,
    };

    if (resolvedAgentId && !usesDefaultAgent) {
      request.agent = resolvedAgentId;
    }

    if (resolvedModelId && !usesDefaultModel) {
      request.model = { providerID: model?.providerId ?? "", modelID: resolvedModelId };
    }

    if (attachments && attachments.length > 0) {
      request.attachments = attachments;
    }

    void postPrompt(promptId, request);

    return true;
  }

  return {
    canSend,
    error: readonly(sendError),
    sendPrompt,
    sentPrompts,
  };
}
