import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { computed, ref, shallowRef } from "vue";
import { useDraftState } from "@/composables/use-draft-state";
import { useComposerActions } from "@/composables/use-composer-actions";

vi.mock("@/api/client", () => ({
  api: { GET: vi.fn(), POST: vi.fn(), PUT: vi.fn(), DELETE: vi.fn(), PATCH: vi.fn() },
}));

// Only the prompt path is under test: the rest of what a composer does is stubbed.
vi.mock("@/composables/use-agents", () => ({
  useAgents: () => ({ agentsById: ref({}), defaultAgentId: computed(() => "") }),
}));
vi.mock("@/composables/use-models", () => ({
  useModels: () => ({ modelsByKey: ref({}), defaultModelKey: computed(() => "") }),
}));
vi.mock("@/lib/track-action", () => ({ trackAction: vi.fn() }));
vi.mock("@/composables/use-harnesses", () => ({ useHarnesses: () => ({ harnesses: ref([]) }) }));
vi.mock("@/composables/use-session-actions", () => ({
  useAbortSession: () => ({ abortSession: vi.fn(), isAborting: ref(false) }),
}));
vi.mock("@/composables/use-run-shell-command", () => ({
  useRunShellCommand: () => ({ error: shallowRef(undefined), runShellCommand: vi.fn() }),
}));
vi.mock("@/composables/use-session-queue", () => ({
  useSessionQueue: () => ({ queue: ref([]), error: shallowRef(undefined), enqueue: vi.fn(), remove: vi.fn(), sendNow: vi.fn() }),
}));
vi.mock("@/composables/use-side-conversation", () => ({
  useSideConversation: () => ({ error: shallowRef(undefined), isOpen: ref(false), ask: vi.fn() }),
}));

import { api } from "@/api/client";

const mockApi = vi.mocked(api);
const refused = () => ({ error: { error: "This session's folder isn't there any more." }, response: new Response(null, { status: 409 }) });
const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

beforeEach(() => setActivePinia(createPinia()));
afterEach(() => mockApi.POST.mockReset());

describe("useComposerActions when the server refuses a send", () => {
  it("puts the text back and tells the composer, so the photos it cleared come back too", async () => {
    const sessionId = "session-phone-refused";
    const draft = useDraftState(sessionId, { agentId: "", modelId: "" });
    draft.setText("what's on this screen?");
    mockApi.POST.mockResolvedValueOnce(refused());
    const onRefused = vi.fn();

    const route = useComposerActions(sessionId).submit({ attachments: [{ mime: "image/png", filename: "shot.png", data: "AAAA" }], onRefused });
    expect(route.kind).toBe("prompt");
    expect(draft.draft.text).toBe("");
    await settle();

    expect(draft.draft.text).toBe("what's on this screen?");
    expect(onRefused).toHaveBeenCalledTimes(1);
  });

  it("puts a held message the server refuses in the box, after what's typed there", async () => {
    const sessionId = "session-phone-held";
    const draft = useDraftState(sessionId, { agentId: "", modelId: "" });
    draft.setText("a new thought");
    mockApi.POST.mockResolvedValueOnce(refused());

    expect(await useComposerActions(sessionId).sendText("written while offline")).toBeNull();
    await settle();

    expect(draft.draft.text).toBe("a new thought\n\nwritten while offline");
  });
});
