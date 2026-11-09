import { effectScope, nextTick, reactive, ref } from "vue";
import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { useEffortMemory } from "@/composables/use-effort-memory";
import { usePreferencesStore } from "@/stores/preferences";

const put = vi.fn().mockResolvedValue({ error: undefined });
vi.mock("@/lib/machine-target", () => ({
  useMachineTarget: () => ({ key: "live", isLive: true, api: { GET: vi.fn().mockResolvedValue({ data: {} }), PUT: put } }),
  liveTarget: () => ({ key: "live", isLive: true, api: { GET: vi.fn().mockResolvedValue({ data: {} }), PUT: put } }),
}));

function setup(stored: Record<string, string>) {
  const pinia = createPinia();
  setActivePinia(pinia);
  const store = usePreferencesStore();
  store.preferences = stored;
  store.hasFetched = true;
  const draft = reactive({ text: "", agentId: "", modelId: "", effort: "" });
  const model = ref("anthropic/opus");
  const variants = ref<string[]>(["low", "medium", "high"]);
  const memory = effectScope().run(() => useEffortMemory({
    draft,
    setEffort: (value) => { draft.effort = value; },
    harnessType: () => "opencode2",
    modelKey: () => model.value,
    modelName: () => model.value === "anthropic/opus" ? "Opus 5.5" : "Haiku",
    picked: () => model.value,
    variants: () => variants.value,
  }))!;
  return { draft, model, variants, memory, store };
}

describe("useEffortMemory", () => {
  beforeEach(() => put.mockClear());

  it("opens a draft on the remembered level, else Default", () => {
    expect(setup({ "effort.opencode2.anthropic:opus": "high" }).draft.effort).toBe("high");
    expect(setup({}).draft.effort).toBe("");
  });

  it("ignores a remembered level the model no longer offers, without deleting it", () => {
    const { draft, store } = setup({ "effort.opencode2.anthropic:opus": "max" });
    expect(draft.effort).toBe("");
    expect(store.preferences["effort.opencode2.anthropic:opus"]).toBe("max");
  });

  it("brings each model's level back on a switch and says so; a model without one resets to Default", async () => {
    const { draft, model, memory } = setup({ "effort.opencode2.anthropic:opus": "high", "effort.opencode2.anthropic:haiku": "low" });
    model.value = "anthropic/haiku";
    await nextTick();
    expect(draft.effort).toBe("low");
    expect(memory.note.value).toBe("Haiku: back on Low");
    model.value = "anthropic/other";
    await nextTick();
    expect(draft.effort).toBe("");
  });

  it("writes only a composer pick; Default stores an empty value", async () => {
    const { draft, memory, store } = setup({});
    expect(put).not.toHaveBeenCalled();
    memory.pick("medium");
    expect(draft.effort).toBe("medium");
    expect(store.preferences["effort.opencode2.anthropic:opus"]).toBe("medium");
    memory.pick("");
    expect(put).toHaveBeenLastCalledWith("/api/preferences/{key}", expect.objectContaining({ body: { value: "" } }));
  });
});
