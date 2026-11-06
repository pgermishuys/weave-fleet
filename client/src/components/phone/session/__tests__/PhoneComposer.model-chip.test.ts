import { computed, ref } from "vue";
import { mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { createModelSelectionKey } from "@/composables/use-models";
import { useSessionsStore } from "@/stores/sessions";

const opus = { id: "claude-opus-4.7", name: "Claude Opus 4.7", providerId: "claude", provider: "Claude", description: "", selectionKey: createModelSelectionKey("claude", "claude-opus-4.7"), variants: ["low", "medium", "high"] };
const haiku = { id: "claude-haiku-4.5", name: "Claude Haiku 4.5", providerId: "claude", provider: "Claude", description: "", selectionKey: createModelSelectionKey("claude", "claude-haiku-4.5"), variants: ["low", "medium", "high"] };

vi.mock("@/composables/use-models", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/composables/use-models")>()),
  useModels: () => ({
    models: computed(() => [opus, haiku]),
    modelsByKey: computed(() => ({ [opus.selectionKey]: opus, [haiku.selectionKey]: haiku })),
    defaultModelKey: computed(() => opus.selectionKey),
  }),
}));
vi.mock("@/composables/use-agents", () => ({ useAgents: () => ({ agents: ref([]), defaultAgentId: ref("") }) }));
vi.mock("@/composables/use-session-permission-rules", () => ({ useSessionPermissionRules: () => ({ rules: ref([]), remove: vi.fn(), removeAll: vi.fn() }) }));
vi.mock("@/composables/use-composer-actions", () => ({
  primaryAction: () => "send",
  useComposerActions: () => ({
    draft: { text: "", agentId: "", modelId: "", effort: "medium" },
    setText: vi.fn(),
    status: ref("idle"),
    caps: ref({ canSteer: false }),
    queue: ref([]),
    error: ref(null),
    canSendQueued: ref(false),
    disabled: ref(false),
  }),
}));
vi.mock("@/composables/use-draft-state", () => ({ useDraftState: () => ({ setAgentId: vi.fn(), setModelId: vi.fn(), setEffort: vi.fn() }) }));

import PhoneComposer from "../PhoneComposer.vue";

function chip(session: Record<string, unknown>): string {
  const pinia = createPinia();
  setActivePinia(pinia);
  useSessionsStore().setSessions([{ session: { id: "s1" }, harnessType: "claude-code", ...session }] as never);
  const wrapper = mount(PhoneComposer, { props: { sessionId: "s1", machineId: "m", machineName: "m" }, global: { plugins: [pinia], stubs: { teleport: true } } });
  return wrapper.find('[data-testid="chip-model"]').text();
}

describe("PhoneComposer model chip", () => {
  beforeEach(() => setActivePinia(createPinia()));

  it("names the model that answered last when the session has none chosen, like desktop's Default (…)", () => {
    expect(chip({ lastAssistantModelId: "claude-haiku-4.5" })).toContain("Default (Claude Haiku 4.5)");
  });

  it("reads Default (…) for the session's own model, as desktop does", () => {
    expect(chip({ selectedModel: { providerID: "claude", modelID: "claude-haiku-4.5" } })).toContain("Default (Claude Haiku 4.5)");
  });
});
