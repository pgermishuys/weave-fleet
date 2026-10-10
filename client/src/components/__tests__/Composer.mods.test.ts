import { flushPromises, mount } from "@vue/test-utils";
import { composerBands, type ComposerBandView } from "@/lib/mods/points";
import type { ModWireElement } from "@/lib/mods/types";
import { beforeEach, describe, expect, it, vi } from "vitest";
import Composer from "@/components/session/Composer.vue";
import { useSessionsStore } from "@/stores/sessions";
import type { SessionListItem } from "@/api/client";
import { forgetHarnessLists } from "@/composables/use-harnesses";

vi.mock("@/api/client", () => ({
  api: {
    GET: vi.fn(),
    POST: vi.fn(),
    PUT: vi.fn(),
    DELETE: vi.fn(),
    PATCH: vi.fn(),
  },
}));

const { sessionsTopicHandlers, sessionTopicHandlers } = vi.hoisted(() => ({
  sessionsTopicHandlers: new Set<(event: unknown) => void>(),
  sessionTopicHandlers: new Map<string, Set<(event: unknown) => void>>(),
}));
vi.mock("@/composables/use-signalr-socket", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/composables/use-signalr-socket")>()),
  onGlobalEvent: (_machine: unknown, topic: string, handler: (event: unknown) => void) => {
    if (topic !== "sessions") return () => {};
    sessionsTopicHandlers.add(handler);
    return () => sessionsTopicHandlers.delete(handler);
  },
  // No hub in tests: a session topic's events are pushed with pushSessionEvent.
  useWeaveSocket: () => ({
    subscribeV2: (topic: string, _onSnapshot: unknown, onEvent: (event: unknown) => void) => {
      const handlers = sessionTopicHandlers.get(topic) ?? new Set();
      sessionTopicHandlers.set(topic, handlers);
      handlers.add(onEvent);
      return () => handlers.delete(onEvent);
    },
  }),
}));

import { api } from "@/api/client";

const mockApi = vi.mocked(api);

function createCapabilities(overrides: Partial<NonNullable<SessionListItem["capabilities"]>> = {}): NonNullable<SessionListItem["capabilities"]> {
  return {
    canPrompt: true,
    canRestart: false,
    canAbort: false,
    canArchive: false,
    canUnarchive: false,
    canFork: true,
    canDelete: true,
    promptDisabledReason: null,
    restartDisabledReason: null,
    abortDisabledReason: null,
    archiveDisabledReason: null,
    unarchiveDisabledReason: null,
    forkDisabledReason: null,
    deleteDisabledReason: null,
    ...overrides,
  };
}

function createSession(overrides: Partial<SessionListItem> = {}): SessionListItem {
  return {
    harnessType: "opencode",
    instanceId: "instance-1",
    workspaceId: "workspace-1",
    workspaceDirectory: "/tmp/workspace",
    workspaceDisplayName: "workspace",
    isolationStrategy: "existing",
    sessionStatus: "active",
    session: {
      id: "session-1",
      title: "Composer session",
      time: {
        created: 1,
        updated: 2,
      },
      tags: [],
    },
    instanceStatus: "running",
    parentSessionId: null,
    sourceDirectory: "/tmp/workspace",
    branch: "main",
    activityStatus: null,
    lifecycleStatus: "running",
    retentionStatus: "active",
    archivedAt: null,
    typedInstanceStatus: "running",
    isHidden: false,
    projectId: "project-1",
    projectName: "Project",
    capabilities: createCapabilities(),
    tags: [],
    ...overrides,
  };
}

let queuedIds = 0;

function configureApiFetch(): void {
  mockApi.GET.mockImplementation(async (url: string) => {
    if (url === "/api/agents") {
      return {
        data: [
          { name: "alpha", description: "Planner", mode: "primary", color: "#ff00aa" },
        ],
        error: undefined,
        response: new Response(),
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any;
    }

    if (url === "/api/sessions/{id}/models") {
      return {
        data: {
          providers: [
            {
              id: "provider-1",
              name: "Provider One",
              models: [{ id: "shared-model", name: "Model 1" }],
            },
            {
              id: "provider-2",
              name: "Provider Two",
              models: [{ id: "shared-model", name: "Model 1" }],
            },
          ],
        },
        error: undefined,
        response: new Response(),
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any;
    }

    if (url === "/api/sessions/{id}/commands") {
      return {
        data: {
          commands: [
            { name: "help", description: "Show help" },
            { name: "status", description: "Show status" },
          ],
        },
        error: undefined,
        response: new Response(JSON.stringify({
          commands: [
            { name: "help", description: "Show help" },
            { name: "status", description: "Show status" },
          ],
        }), { headers: { "Content-Type": "application/json" } }),
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any;
    }

    if (url === "/api/sessions/{id}/agents") {
      return {
        data: [
          { name: "alpha", description: "Planner", mode: "primary", color: "#ff00aa" },
        ],
        error: undefined,
        response: new Response(),
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any;
    }

    if (url === "/api/instances/{instanceId}/find/files") {
      return {
        data: {
          instanceId: "instance-1",
          files: ["src/main.ts"],
        },
        error: undefined,
        response: new Response(),
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any;
    }

    if (url === "/api/sessions/{id}/queue") {
      return { data: [], error: undefined, response: new Response() } as never;
    }

    throw new Error(`Unhandled GET call: ${url}`);
  });

  mockApi.DELETE.mockImplementation((async (url: string) => {
    if (url === "/api/sessions/{id}/queue/{itemId}") {
      return { data: undefined, error: undefined, response: new Response(null, { status: 204 }) };
    }
    throw new Error(`Unhandled DELETE call: ${url}`);
  }) as never);

  mockApi.POST.mockImplementation(async (url: string, init?: unknown) => {
    // Fleet keeps the queue: it answers with the item it queued.
    if (url === "/api/sessions/{id}/queue") {
      const body = (init as { body: { text: string; kind: string } }).body;
      return {
        data: { id: `queued-${++queuedIds}`, kind: body.kind, text: body.text, createdAt: "2026-09-25T10:00:00Z" },
        error: undefined,
        response: new Response(null, { status: 201 }),
      } as never;
    }

    if (url === "/api/sessions/{id}/queue/{itemId}/send") {
      return { data: undefined, error: undefined, response: new Response(null, { status: 202 }) } as never;
    }

    if (url === "/api/sessions/{id}/prompt") {
      return {
        data: {},
        error: undefined,
        response: new Response(),
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any;
    }

    if (url === "/api/sessions/{id}/command") {
      return {
        data: {},
        error: undefined,
        response: new Response(null, { status: 202 }),
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any;
    }

    if (url === "/api/telemetry/actions") {
      return {
        data: {},
        error: undefined,
        response: new Response(),
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any;
    }

    throw new Error(`Unhandled POST call: ${url}`);
  });
}

interface MountComposerOptions {
  sessionId?: string;
  instanceId?: string;
  session?: SessionListItem;
  disabled?: boolean;
}

function mountComposer(options: MountComposerOptions = {}) {
  const sessionsStore = useSessionsStore();
  const session = options.session ?? createSession();
  sessionsStore.setSessions([session]);
  sessionsStore.setActiveSessionId("session-1");

  return mount(Composer, {
    attachTo: document.body,
    props: {
      sessionId: options.sessionId ?? "session-1",
      instanceId: options.instanceId ?? "instance-1",
      disabled: options.disabled,
    },
    global: {
      stubs: {
        AgentSelector: {
          template: "<div data-testid=\"agent-selector\" />",
        },
        ModelSelector: {
          props: ["modelValue", "models"],
          emits: ["update:modelValue"],
          methods: {
            handleChange(event: Event) {
              this.$emit(
                "update:modelValue",
                (event.target as HTMLSelectElement).value,
              );
            },
          },
          template: `
            <select
              data-testid="model-selector"
              :value="modelValue"
              @change="handleChange"
            >
              <option value="">Default</option>
              <option v-for="model in models" :key="model.selectionKey" :value="model.selectionKey">
                {{ model.providerId }}::{{ model.id }}
              </option>
            </select>
          `,
        },
      },
    },
  });
}

const text = (value: string): ModWireElement => ({ type: "Text", props: {}, children: [value] });
const mods = (draft = false) => [{ name: "Band mod", draft }];

function contribute(sessionId: string, tree: unknown, extra: Partial<ComposerBandView> = {}) {
  return composerBands.contribute("test", [{ sessionId, tree: tree as ModWireElement | null, mods: mods(), ...extra }]);
}

/** Mods at the composer band. (No contribution = today's DOM: Composer.characterise.test.ts.) */
describe("Composer band", () => {
  beforeEach(() => {
    forgetHarnessLists();
    mockApi.GET.mockReset();
    mockApi.POST.mockReset();
    configureApiFetch();
    composerBands.clear();
  });

  it("draws the band after the background strip and before the composer frame", async () => {
    contribute("session-1", text("Lint clean"));
    const wrapper = mountComposer();
    await flushPromises();
    const band = wrapper.find("[data-testid=mod-composer-band]");
    expect(band.exists()).toBe(true);
    expect(band.text()).toContain("Lint clean");
    expect(band.element.nextElementSibling?.classList.contains("composer-frame")).toBe(true);
    expect(wrapper.find("[data-testid=prompt-input]").exists()).toBe(true);
    wrapper.unmount();
  });

  it("draws only its own session's band", async () => {
    contribute("session-2", text("Other"));
    const wrapper = mountComposer();
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-composer-band]").exists()).toBe(false);
    wrapper.unmount();
  });

  it("restores today's DOM when the band is removed", async () => {
    const wrapper = mountComposer();
    await flushPromises();
    const before = wrapper.html();
    const remove = contribute("session-1", text("Here"));
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-composer-band]").exists()).toBe(true);
    remove();
    await flushPromises();
    expect(wrapper.html()).toBe(before);
    wrapper.unmount();
  });

  it("draws nothing for a null tree or an invalid one", async () => {
    contribute("session-1", null);
    const wrapper = mountComposer();
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-composer-band]").exists()).toBe(false);
    composerBands.clear();
    contribute("session-1", { type: "Nope", props: {} });
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-composer-band]").exists()).toBe(false);
    wrapper.unmount();
  });

  it("marks a draft mod's band and sends actions from the desktop", async () => {
    const onAction = vi.fn();
    contribute("session-1", { type: "Button", props: { key: "go", label: "Go" }, handles: { onPress: "h1" } }, { mods: mods(true), onAction });
    const wrapper = mountComposer();
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-composer-band] [data-testid=mod-draft-mark]").exists()).toBe(true);
    await wrapper.find("[data-testid=mod-composer-band] button").trigger("click");
    expect(onAction).toHaveBeenCalledTimes(1);
    expect(onAction.mock.calls[0]?.[1]).toBe("desktop");
    wrapper.unmount();
  });

  it("has no draft mark for a kept mod", async () => {
    contribute("session-1", text("Kept"));
    const wrapper = mountComposer();
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-draft-mark]").exists()).toBe(false);
    wrapper.unmount();
  });
});
