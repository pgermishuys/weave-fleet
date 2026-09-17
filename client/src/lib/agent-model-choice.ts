import type { HarnessCatalog, ModelReference } from "@/api/client";
import type { AgentOption } from "@/composables/use-agents";
import { createModelSelectionKey, type ModelOption } from "@/composables/use-models";

/**
 * The agent and model a session (or an automation's run) starts with, as the pickers hold them: an agent name and
 * a model selection key, each empty for "Default".
 */
export interface AgentModelChoice {
  agent: string;
  model: string;
}

export const DEFAULT_CHOICE: AgentModelChoice = { agent: "", model: "" };

/** A model selection key back to the model it names; null for "Default" or a key that isn't one. */
export function modelFromKey(key: string): ModelReference | null {
  if (!key) {
    return null;
  }
  try {
    const parsed: unknown = JSON.parse(key);
    if (Array.isArray(parsed) && parsed.length === 2 && parsed.every((part) => typeof part === "string" && part.length > 0)) {
      return { providerID: parsed[0] as string, modelID: parsed[1] as string };
    }
  } catch {
    // Not a selection key.
  }
  return null;
}

export function keyForModel(model: ModelReference): string {
  return createModelSelectionKey(model.providerID, model.modelID);
}

/** `provider/model`, the way automations store a model; split back at the first slash. */
export function modelToPath(model: ModelReference | null): string | null {
  return model ? `${model.providerID}/${model.modelID}` : null;
}

export function modelFromPath(path: string | null | undefined): ModelReference | null {
  const slash = path?.indexOf("/") ?? -1;
  if (!path || slash <= 0 || slash === path.length - 1) {
    return null;
  }
  return { providerID: path.slice(0, slash), modelID: path.slice(slash + 1) };
}

/** Keeps only what the catalog still offers: an agent or model that's gone goes back to "Default". */
export function keepOffered(choice: AgentModelChoice, catalog: HarnessCatalog): AgentModelChoice {
  const agentOffered = !choice.agent || catalog.agents.some((agent) => agent.name === choice.agent && !agent.hidden);
  const model = modelFromKey(choice.model);
  const modelOffered = !model || catalog.providers.some((provider) => provider.id === model.providerID
    && provider.models.some((candidate) => candidate.id === model.modelID));
  return {
    agent: agentOffered ? choice.agent : "",
    model: modelOffered ? choice.model : "",
  };
}

/** The model a prompt gets from `agent` (or the default agent) when it names none; null when only the harness knows. */
export function modelFor(catalog: HarnessCatalog, agent: string): ModelReference | null {
  const name = agent || catalog.defaultAgent;
  const own = catalog.agents.find((candidate) => candidate.name === name)?.model;
  return own && own.providerID && own.modelID ? own : catalog.defaultModel;
}

/** A model's name from the catalog, or its id when the catalog doesn't list it (e.g. a provider with no key). */
export function modelName(catalog: HarnessCatalog, model: ModelReference): string {
  const provider = catalog.providers.find((candidate) => candidate.id === model.providerID);
  return provider?.models.find((candidate) => candidate.id === model.modelID)?.name ?? model.modelID;
}

/**
 * A model id on its own — what a message carries — as a name you can read. Falls back to the id when the session's
 * catalog doesn't list it (a provider with no key), and to nothing when there is no id at all.
 */
export function modelDisplayName(modelId: string | null | undefined, models: readonly ModelOption[]): string {
  if (!modelId) {
    return "";
  }
  return models.find((candidate) => candidate.id === modelId)?.name ?? modelId;
}

/** What "Default" means on each picker, e.g. "Default (loom)" and "Default (Claude Opus 4.7)". */
export interface DefaultLabels {
  agentLabel: string;
  agentDescription: string;
  modelLabel: string;
  modelDescription: string;
}

export function describeDefaults(catalog: HarnessCatalog | null, choice: AgentModelChoice): DefaultLabels {
  const defaultAgent = catalog?.defaultAgent ?? null;
  const agentLabel = defaultAgent ? `Default (${defaultAgent})` : "Default";
  const agentDescription = defaultAgent
    ? `The agent this folder uses when you don't pick one: ${defaultAgent}`
    : "The agent this folder uses when you don't pick one";

  const model = catalog ? modelFor(catalog, choice.agent) : null;
  if (!catalog || !model) {
    return { agentLabel, agentDescription, modelLabel: "Default", modelDescription: "Whatever the harness picks" };
  }

  const agent = choice.agent || defaultAgent;
  const agentsOwn = catalog.agents.some((candidate) => candidate.name === agent && candidate.model?.modelID);
  const name = modelName(catalog, model);
  return {
    agentLabel,
    agentDescription,
    modelLabel: `Default (${name})`,
    modelDescription: agentsOwn ? `${agent}'s own model: ${name}` : `This folder's model: ${name}`,
  };
}

/**
 * What "Default" means inside a session: the agent and model it started with or was last given (prompts that name
 * none get them), else the harness's own. `draftAgent` is an agent picked for the next prompt, whose own model
 * applies when the session has none.
 */
export function describeSessionDefaults(input: {
  sessionAgent: string | null | undefined;
  sessionModel: ModelReference | null | undefined;
  draftAgent: string;
  defaultAgent: string;
  agents: readonly AgentOption[];
  models: readonly ModelOption[];
}): DefaultLabels {
  const agent = input.sessionAgent || input.defaultAgent;
  const agentLabel = agent ? `Default (${agent})` : "Default";
  const agentDescription = input.sessionAgent
    ? `This session's agent: ${input.sessionAgent}`
    : "Use the session default agent";

  const forAgent = input.draftAgent || agent;
  const agentsOwn = input.agents.find((candidate) => candidate.id === forAgent)?.model;
  const model = input.sessionModel ?? agentsOwn ?? null;
  if (!model) {
    return { agentLabel, agentDescription, modelLabel: "Default", modelDescription: "Use the session default model" };
  }

  const name = input.models.find((candidate) => candidate.providerId === model.providerID && candidate.id === model.modelID)?.name
    ?? model.modelID;
  return {
    agentLabel,
    agentDescription,
    modelLabel: `Default (${name})`,
    modelDescription: input.sessionModel ? `This session's model: ${name}` : `${forAgent}'s own model: ${name}`,
  };
}
