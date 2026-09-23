import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { shallowRef } from "vue";
import { useAgents } from "@/composables/use-agents";
import { useAutocomplete } from "@/composables/use-autocomplete";
import { useModels } from "@/composables/use-models";
import type { DomainEvent } from "@/lib/domain-events";
import { HARNESS_CATALOG_CHANGED } from "@/lib/harness-catalog-changes";
import { flushAll, mountComposable } from "./test-utils";

const { mockApi, handlers } = vi.hoisted(() => ({
  mockApi: { GET: vi.fn() },
  handlers: new Set<(event: DomainEvent) => void>(),
}));

vi.mock("@/api/client", () => ({ api: mockApi }));
vi.mock("@/lib/api-client", () => ({ apiFetch: vi.fn() }));
vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: (_topic: string, handler: (event: DomainEvent) => void) => {
    handlers.add(handler);
    return () => handlers.delete(handler);
  },
}));

function push(sessionIds: string[]): void {
  const event = {
    type: HARNESS_CATALOG_CHANGED,
    payload: { harnessType: "opencode2", directory: "/work/rocket", quickChat: false, profileIds: ["none"], sessionIds },
  } as unknown as DomainEvent;
  for (const handler of [...handlers]) handler(event);
}

const ok = (data: unknown) => ({ data, error: undefined, response: new Response(null, { status: 200 }) });
const down = () => ({ data: undefined, error: { error: "down" }, response: new Response(null, { status: 502 }) });

/** What the session's harness offers, changed by the test between asks; "down" answers with an error. */
let offered: { agents: string[]; models: string[] } | "down";

function answer(): void {
  mockApi.GET.mockImplementation(async (url: string) => {
    if (offered === "down") return down();
    if (url === "/api/sessions/{id}/agents") {
      return ok({ agents: offered.agents.map((name) => ({ name, mode: "primary", hidden: false })) });
    }
    if (url === "/api/sessions/{id}/models") {
      return ok({ providers: [{ id: "fake", name: "Fake", models: offered.models.map((id) => ({ id, name: id })) }] });
    }
    if (url === "/api/sessions/{id}/commands") return ok({ commands: [] });
    return ok({});
  });
}

const asked = (url: string) => mockApi.GET.mock.calls.filter(([called]) => called === url).length;

/** A session's pickers as an open conversation has them: the composer's, the header's and the prompt sender's. */
async function mountPickers(sessionId = "s1") {
  return mountComposable(() => ({
    composerAgents: useAgents(sessionId),
    senderAgents: useAgents(sessionId),
    composerModels: useModels(sessionId),
    headerModels: useModels(() => sessionId),
    senderModels: useModels(sessionId),
    autocomplete: useAutocomplete({
      value: shallowRef("@"),
      setValue: () => {},
      sessionId,
      inputRef: shallowRef(null),
      cursorPosition: shallowRef(1),
    }),
  }));
}

describe("a session's agent and model pickers and pushed catalog changes", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    mockApi.GET.mockReset();
    handlers.clear();
    offered = { agents: ["build"], models: ["small"] };
    answer();
  });

  it("ask once when the harness says the session's catalog changed, however many pickers show it", async () => {
    const { result } = await mountPickers();
    expect(asked("/api/sessions/{id}/agents")).toBe(1);
    expect(asked("/api/sessions/{id}/models")).toBe(1);

    offered = { agents: ["build", "reviewer"], models: ["small", "large"] };
    push(["s1"]);
    await flushAll();

    expect(asked("/api/sessions/{id}/agents")).toBe(2);
    expect(asked("/api/sessions/{id}/models")).toBe(2);
    expect(result.composerAgents.agents.value.map((agent) => agent.id)).toEqual(["build", "reviewer"]);
    expect(result.senderAgents.agentsById.value.reviewer?.name).toBe("reviewer");
    expect(result.composerModels.models.value.map((model) => model.id)).toEqual(["small", "large"]);
    expect(result.headerModels.models.value.map((model) => model.id)).toEqual(["small", "large"]);
  });

  it("ignore a change that doesn't name the session", async () => {
    await mountPickers();

    push(["s2"]);
    push([]);
    await flushAll();

    expect(asked("/api/sessions/{id}/agents")).toBe(1);
    expect(asked("/api/sessions/{id}/models")).toBe(1);
  });

  it("keep the list up while asking again, and when asking again fails", async () => {
    const { result } = await mountPickers();

    offered = "down";
    push(["s1"]);
    expect(result.composerAgents.isLoading.value).toBe(false);
    expect(result.composerModels.isLoading.value).toBe(false);
    await flushAll();

    expect(result.composerAgents.agents.value.map((agent) => agent.id)).toEqual(["build"]);
    expect(result.composerModels.models.value.map((model) => model.id)).toEqual(["small"]);

    offered = { agents: ["build", "reviewer"], models: ["small"] };
    push(["s1"]);
    await flushAll();

    expect(result.composerAgents.agents.value.map((agent) => agent.id)).toEqual(["build", "reviewer"]);
    expect(result.composerAgents.error.value).toBeUndefined();
  });

  it("stop listening when the session's view goes", async () => {
    const { wrapper } = await mountPickers();
    wrapper.unmount();

    push(["s1"]);
    await flushAll();

    expect(asked("/api/sessions/{id}/agents")).toBe(1);
    expect(asked("/api/sessions/{id}/models")).toBe(1);
  });
});
