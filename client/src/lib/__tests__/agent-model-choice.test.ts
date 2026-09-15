import { describe, expect, it } from "vitest";
import type { HarnessCatalog } from "@/api/client";
import {
  describeDefaults,
  describeSessionDefaults,
  keepOffered,
  keyForModel,
  modelFromKey,
  modelFromPath,
  modelToPath,
} from "@/lib/agent-model-choice";

const catalog: HarnessCatalog = {
  supported: true,
  agents: [
    { name: "loom", mode: "primary", model: { providerID: "github-copilot", modelID: "claude-opus-4.7" } },
    { name: "build", mode: "primary" },
    { name: "title", mode: "primary", hidden: true },
  ],
  providers: [
    {
      id: "github-copilot",
      name: "GitHub Copilot",
      models: [
        { id: "claude-opus-4.7", name: "Claude Opus 4.7" },
        { id: "claude-haiku-4.5", name: "Claude Haiku 4.5" },
      ],
    },
  ],
  defaultAgent: "loom",
  defaultModel: null,
};

describe("agent-model-choice", () => {
  it("turns a model selection key back into the model", () => {
    const key = keyForModel({ providerID: "openrouter", modelID: "anthropic/claude-haiku-4.5" });

    expect(modelFromKey(key)).toEqual({ providerID: "openrouter", modelID: "anthropic/claude-haiku-4.5" });
    expect(modelFromKey("")).toBeNull();
    expect(modelFromKey("not json")).toBeNull();
    expect(modelFromKey(JSON.stringify(["only-one"]))).toBeNull();
  });

  it("writes a model as provider/model and reads it back at the first slash", () => {
    expect(modelToPath({ providerID: "openrouter", modelID: "anthropic/claude-haiku-4.5" })).toBe("openrouter/anthropic/claude-haiku-4.5");
    expect(modelFromPath("openrouter/anthropic/claude-haiku-4.5")).toEqual({ providerID: "openrouter", modelID: "anthropic/claude-haiku-4.5" });
    expect(modelFromPath("claude-haiku-4.5")).toBeNull();
    expect(modelFromPath(null)).toBeNull();
  });

  it("keeps what the catalog offers and drops what it doesn't", () => {
    const haiku = keyForModel({ providerID: "github-copilot", modelID: "claude-haiku-4.5" });
    const gone = keyForModel({ providerID: "anthropic", modelID: "claude-haiku-4-5" });

    expect(keepOffered({ agent: "build", model: haiku }, catalog)).toEqual({ agent: "build", model: haiku });
    expect(keepOffered({ agent: "retired", model: gone }, catalog)).toEqual({ agent: "", model: "" });
    expect(keepOffered({ agent: "title", model: "" }, catalog)).toEqual({ agent: "", model: "" });
  });

  it("says what Default resolves to", () => {
    expect(describeDefaults(catalog, { agent: "", model: "" })).toMatchObject({
      agentLabel: "Default (loom)",
      modelLabel: "Default (Claude Opus 4.7)",
      modelDescription: "loom's own model: Claude Opus 4.7",
    });
  });

  it("says plain Default when only the harness knows the model", () => {
    expect(describeDefaults(catalog, { agent: "build", model: "" })).toMatchObject({
      modelLabel: "Default",
      modelDescription: "Whatever the harness picks",
    });
  });

  it("uses the folder's configured model for an agent without one", () => {
    const configured = { ...catalog, defaultModel: { providerID: "github-copilot", modelID: "claude-haiku-4.5" } };

    expect(describeDefaults(configured, { agent: "build", model: "" })).toMatchObject({
      modelLabel: "Default (Claude Haiku 4.5)",
      modelDescription: "This folder's model: Claude Haiku 4.5",
    });
  });

  it("says plain Default before the catalog arrives", () => {
    expect(describeDefaults(null, { agent: "", model: "" })).toMatchObject({ agentLabel: "Default", modelLabel: "Default" });
  });

  describe("in a session", () => {
    const agents = [
      { id: "loom", name: "loom", description: "", model: { providerID: "github-copilot", modelID: "claude-opus-4.7" } },
      { id: "build", name: "build", description: "" },
    ];
    const models = [
      { id: "claude-opus-4.7", name: "Claude Opus 4.7", providerId: "github-copilot", selectionKey: "", provider: "GitHub Copilot", description: "" },
      { id: "claude-haiku-4.5", name: "Claude Haiku 4.5", providerId: "github-copilot", selectionKey: "", provider: "GitHub Copilot", description: "" },
    ];

    it("Default is the agent and model the session was given", () => {
      expect(describeSessionDefaults({
        sessionAgent: "build",
        sessionModel: { providerID: "github-copilot", modelID: "claude-haiku-4.5" },
        draftAgent: "",
        defaultAgent: "loom",
        agents,
        models,
      })).toEqual({
        agentLabel: "Default (build)",
        agentDescription: "This session's agent: build",
        modelLabel: "Default (Claude Haiku 4.5)",
        modelDescription: "This session's model: Claude Haiku 4.5",
      });
    });

    it("without a choice of its own, Default is the harness's agent and that agent's model", () => {
      expect(describeSessionDefaults({ sessionAgent: null, sessionModel: null, draftAgent: "", defaultAgent: "loom", agents, models }))
        .toMatchObject({ agentLabel: "Default (loom)", modelLabel: "Default (Claude Opus 4.7)", modelDescription: "loom's own model: Claude Opus 4.7" });
    });

    it("an agent picked for the next prompt brings its own model", () => {
      expect(describeSessionDefaults({ sessionAgent: null, sessionModel: null, draftAgent: "build", defaultAgent: "loom", agents, models }))
        .toMatchObject({ modelLabel: "Default" });
    });
  });
});
