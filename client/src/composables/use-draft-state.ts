import { reactive } from "vue";

export type EffortLevel = "low" | "medium" | "high";

export interface DraftState {
  text: string;
  agentId: string;
  modelId: string;
  effort: EffortLevel;
}

interface DraftDefaults {
  agentId: string;
  modelId: string;
  effort?: EffortLevel;
}

const draftRegistry = reactive<Record<string, DraftState>>({});

function ensureDraft(sessionId: string, defaults: DraftDefaults): DraftState {
  const existingDraft = draftRegistry[sessionId];
  if (existingDraft) {
    if (!existingDraft.agentId) {
      existingDraft.agentId = defaults.agentId;
    }

    if (!existingDraft.modelId) {
      existingDraft.modelId = defaults.modelId;
    }

    return existingDraft;
  }

  const draftState: DraftState = {
    text: "",
    agentId: defaults.agentId,
    modelId: defaults.modelId,
    effort: defaults.effort ?? "medium",
  };

  draftRegistry[sessionId] = draftState;
  return draftRegistry[sessionId];
}

export function useDraftState(sessionId: string, defaults: DraftDefaults) {
  const draft = ensureDraft(sessionId, defaults);

  function setText(text: string): void {
    draft.text = text;
  }

  function setAgentId(agentId: string): void {
    draft.agentId = agentId;
  }

  function setModelId(modelId: string): void {
    draft.modelId = modelId;
  }

  function setEffort(effort: EffortLevel): void {
    draft.effort = effort;
  }

  function resetText(): void {
    draft.text = "";
  }

  return {
    draft,
    setText,
    setAgentId,
    setModelId,
    setEffort,
    resetText,
  };
}

export function clearDraftText(sessionId: string): void {
  const draft = draftRegistry[sessionId];

  if (!draft) {
    return;
  }

  draft.text = "";
}
