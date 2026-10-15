import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import AgentSelector from "@/components/session/AgentSelector.vue";
import ModelSelector from "@/components/session/ModelSelector.vue";
import { createModelSelectionKey } from "@/composables/use-models";

const agents = [{ id: "build", name: "build", description: "" }];
const models = [{
  id: "small",
  name: "Small",
  providerId: "fake",
  selectionKey: createModelSelectionKey("fake", "small"),
  provider: "Fake",
  description: "",
}];

describe("agent and model pickers", () => {
  it("name a listed pick and Default as before", () => {
    expect(mount(AgentSelector, { props: { agents, modelValue: "build" } }).text()).toContain("build");
    expect(mount(AgentSelector, { props: { agents, modelValue: "", defaultLabel: "Default (build)" } }).text())
      .toContain("Default (build)");
    expect(mount(ModelSelector, { props: { models, modelValue: models[0]!.selectionKey } }).text()).toContain("Small");
  });

  it("still name a pick the harness no longer lists", () => {
    expect(mount(AgentSelector, { props: { agents, modelValue: "removed-agent" } }).text()).toContain("removed-agent");
    expect(mount(ModelSelector, { props: { models, modelValue: createModelSelectionKey("gone", "gone-model") } }).text())
      .toContain("gone-model");
  });

  it("tell HydraFusion's cost note under its entry and under a Default that is HydraFusion", async () => {
    const note = "HydraFusion runs several models per prompt; cost varies per prompt.";
    const hydra = { ...models[0]!, id: "hydrafusion", name: "HydraFusion", selectionKey: createModelSelectionKey("copilot", "hydrafusion") };
    const listed = mount(ModelSelector, { props: { models: [...models, hydra], modelValue: "" } });
    await listed.get("button").trigger("click");
    expect(listed.text().split(note)).toHaveLength(2);

    const asDefault = mount(ModelSelector, { props: { models, modelValue: "", defaultLabel: "Default (HydraFusion)" } });
    await asDefault.get("button").trigger("click");
    expect(asDefault.text()).toContain(note);

    const plain = mount(ModelSelector, { props: { models, modelValue: "" } });
    await plain.get("button").trigger("click");
    expect(plain.text()).not.toContain(note);
  });
});
