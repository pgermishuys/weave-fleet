import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import { computed, shallowRef } from "vue";
import type { AccumulatedMessage } from "@/lib/client-types";
import TurnsCanvas from "@/components/canvas/TurnsCanvas.vue";

const messages = shallowRef<readonly AccumulatedMessage[]>([]);

vi.mock("@/composables/use-session-stream", () => ({
  useSessionStream: () => ({
    messages: computed(() => messages.value),
    delegations: computed(() => []),
    hasMore: computed(() => false),
    isLoadingOlder: computed(() => false),
    loadOlder: () => undefined,
  }),
}));

vi.mock("@/composables/use-models", async () => {
  const { shallowRef: ref } = await import("vue");
  return { useModels: () => ({ models: ref([]) }) };
});

function userMessage(id: string, text: string, createdAt: number): AccumulatedMessage {
  return { messageId: id, sessionId: "s1", role: "user", createdAt, parts: [{ partId: `${id}-p`, type: "text", text }] };
}

function assistantMessage(id: string, text: string, createdAt: number): AccumulatedMessage {
  return {
    messageId: id,
    sessionId: "s1",
    role: "assistant",
    createdAt,
    parts: [{ partId: `${id}-p`, type: "text", text }],
  };
}

describe("TurnsCanvas", () => {
  it("asks the conversation to scroll to the round's first message", async () => {
    messages.value = [
      userMessage("u1", "Rename the helper", 1),
      assistantMessage("a1", "Renamed it", 2),
      userMessage("u2", "Add a test", 3),
      assistantMessage("a2", "Added it", 4),
    ];
    // The plumbing for hearing the request lives here only, so the expectations don't depend on it.
    const heard: unknown[] = [];
    const listener = (event: Event) => heard.push((event as CustomEvent).detail);
    window.addEventListener("weave:command-show-message", listener);

    const wrapper = mount(TurnsCanvas, { props: { sessionId: "s1" } });
    await wrapper.get(".turn__jump").trigger("click");
    window.removeEventListener("weave:command-show-message", listener);

    // The newest round is open to begin with, so its button is the one on screen.
    expect(heard).toEqual([{ sessionId: "s1", messageId: "u2" }]);
  });
});
