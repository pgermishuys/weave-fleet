import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { useSessionsStore } from "@/stores/sessions";
import Composer from "@/components/session/Composer.vue";
import type { SessionListItem } from "@/api/client";
import { createModelSelectionKey } from "@/composables/use-models";
import { addDraftTerminalContext, clearDraftTerminalContext } from "@/composables/use-draft-terminal-context";
import { _resetSideConversationsForTesting, useSideConversation } from "@/composables/use-side-conversation";

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
  onGlobalEvent: (topic: string, handler: (event: unknown) => void) => {
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

function pushSessionEvent(sessionId: string, event: unknown): void {
  for (const handler of [...(sessionTopicHandlers.get(`session:${sessionId}`) ?? [])]) handler(event);
}

function pushCatalogChange(sessionIds: string[]): void {
  const event = {
    type: "harness.catalog_changed",
    payload: { harnessType: "opencode2", directory: "/tmp/workspace", profileIds: ["none"], sessionIds },
  };
  for (const handler of [...sessionsTopicHandlers]) handler(event);
}

import { api } from "@/api/client";
import { useDraftState } from "@/composables/use-draft-state";

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

describe("Composer", () => {
  beforeEach(() => {
    mockApi.GET.mockReset();
    mockApi.POST.mockReset();
    configureApiFetch();
  });

  it("keeps textarea focus when clicking an autocomplete item and applies the selection", async () => {
    const wrapper = mountComposer();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    await textarea.setValue("/");
    await flushPromises();

    (textarea.element as HTMLTextAreaElement).focus();
    expect(document.activeElement).toBe(textarea.element);

    const firstItem = wrapper.get(".autocomplete-popup__item");
    await firstItem.trigger("mousedown");
    expect(document.activeElement).toBe(textarea.element);

    await firstItem.trigger("click");
    await flushPromises();

    expect((textarea.element as HTMLTextAreaElement).value).toBe("/help ");
    expect(document.activeElement).toBe(textarea.element);
  });

  it("sends on Enter when the popup is closed", async () => {
    const wrapper = mountComposer();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    await textarea.setValue("Hello there");

    const enterEvent = new KeyboardEvent("keydown", {
      key: "Enter",
      bubbles: true,
      cancelable: true,
    });

    textarea.element.dispatchEvent(enterEvent);
    await flushPromises();

    expect(enterEvent.defaultPrevented).toBe(true);
    expect(mockApi.POST).toHaveBeenCalledWith(
      "/api/sessions/{id}/prompt",
      expect.objectContaining({
        params: expect.objectContaining({
          path: { id: "session-1" },
        }),
      })
    );
    expect(wrapper.emitted("promptSent")).toHaveLength(1);
  });

  it("sends terminal lines ahead of the message, then drops their chips", async () => {
    clearDraftTerminalContext("session-1");
    addDraftTerminalContext("session-1", {
      terminalId: "t1",
      label: "zsh",
      from: 9,
      to: 11,
      text: "FAIL  use-sessions.test.ts\nAssertionError: expected 1, got 2",
    });
    const wrapper = mountComposer();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    expect(wrapper.get(".terminal-context-chip").text()).toContain("zsh");
    expect(wrapper.get(".terminal-context-chip__range").text()).toBe("lines 9–11");

    await textarea.setValue("Why does this fail?");
    textarea.element.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
    await flushPromises();

    expect(mockApi.POST).toHaveBeenCalledWith(
      "/api/sessions/{id}/prompt",
      expect.objectContaining({
        body: expect.objectContaining({
          text: "Terminal zsh, lines 9–11:\n```text\nFAIL  use-sessions.test.ts\nAssertionError: expected 1, got 2\n```\n\nWhy does this fail?",
        }),
      }),
    );
    expect(wrapper.find(".terminal-context-chip").exists()).toBe(false);
  });

  it("sends terminal lines with nothing typed", async () => {
    clearDraftTerminalContext("session-1");
    addDraftTerminalContext("session-1", { terminalId: "t1", label: "zsh", from: 3, to: 3, text: "npm ERR! missing script" });
    const wrapper = mountComposer();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    textarea.element.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
    await flushPromises();

    expect(mockApi.POST).toHaveBeenCalledWith(
      "/api/sessions/{id}/prompt",
      expect.objectContaining({
        body: expect.objectContaining({ text: "Terminal zsh, line 3:\n```text\nnpm ERR! missing script\n```" }),
      }),
    );
  });

  it("removes terminal lines from the message", async () => {
    clearDraftTerminalContext("session-1");
    addDraftTerminalContext("session-1", { terminalId: "t1", label: "zsh", from: 3, to: 3, text: "x" });
    const wrapper = mountComposer();

    await wrapper.get(".terminal-context-chip__remove").trigger("click");

    expect(wrapper.find(".terminal-context-chip").exists()).toBe(false);
  });

  it("routes slash commands to the command endpoint", async () => {
    const wrapper = mountComposer();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    await textarea.setValue("/start-work now");

    const enterEvent = new KeyboardEvent("keydown", {
      key: "Enter",
      bubbles: true,
      cancelable: true,
    });

    textarea.element.dispatchEvent(enterEvent);
    await flushPromises();

    expect(enterEvent.defaultPrevented).toBe(true);
    expect(mockApi.POST).toHaveBeenCalledWith(
      "/api/sessions/{id}/command",
      expect.objectContaining({
        params: expect.objectContaining({
          path: { id: "session-1" },
        }),
      })
    );
    expect(wrapper.emitted("promptSent")).toHaveLength(1);
  });

  it("preserves the selected provider when providers share a model id", async () => {
    const wrapper = mountComposer();
    await flushPromises();

    const modelSelector = wrapper.get("[data-testid='model-selector']");
    await modelSelector.setValue(createModelSelectionKey("provider-2", "shared-model"));

    const textarea = wrapper.get("[data-testid='prompt-input']");
    await textarea.setValue("Hello there");

    const enterEvent = new KeyboardEvent("keydown", {
      key: "Enter",
      bubbles: true,
      cancelable: true,
    });

    textarea.element.dispatchEvent(enterEvent);
    await flushPromises();

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const promptCall = (mockApi.POST.mock.calls as any[]).find(([url]) => url === "/api/sessions/{id}/prompt");
    expect(promptCall).toBeTruthy();
    const [, options] = promptCall!;
    const body = options?.body as { model?: { providerID: string; modelID: string } };
    expect(body.model).toEqual({ providerID: "provider-2", modelID: "shared-model" });
  });

  it("keeps a picked model the harness stops offering while the session is open", async () => {
    const wrapper = mountComposer({ sessionId: "session-live-catalog" });
    await flushPromises();

    const picked = createModelSelectionKey("provider-2", "shared-model");
    await wrapper.get("[data-testid='model-selector']").setValue(picked);

    const answerAsBefore = mockApi.GET.getMockImplementation() as unknown as (url: string) => Promise<unknown>;
    mockApi.GET.mockImplementation((async (url: string) => {
      if (url !== "/api/sessions/{id}/models") return answerAsBefore(url);
      return {
        data: { providers: [{ id: "provider-1", name: "Provider One", models: [{ id: "shared-model", name: "Model 1" }] }] },
        error: undefined,
        response: new Response(),
      };
    }) as never);
    const askedForModels = () =>
      (mockApi.GET.mock.calls as unknown[][]).filter(([url]) => url === "/api/sessions/{id}/models").length;
    const before = askedForModels();

    pushCatalogChange(["session-live-catalog"]);
    await flushPromises();

    expect(askedForModels()).toBe(before + 1);
    expect(wrapper.findAll("[data-testid='model-selector'] option").map((option) => option.text())).toEqual([
      "Default",
      "provider-1::shared-model",
    ]);
    expect(useDraftState("session-live-catalog", { agentId: "", modelId: "" }).draft.modelId).toBe(picked);
    wrapper.unmount();
  });

  it("marks @ references behind the text once they're typed, and sends them as plain text", async () => {
    const wrapper = mountComposer();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    await textarea.setValue("look at @src/ap");
    expect(wrapper.findAll(".composer-reference")).toHaveLength(0);

    await textarea.setValue("look at @src/app.ts and @docs/ please");
    expect(wrapper.findAll(".composer-reference").map((reference) => reference.text())).toEqual(["@src/app.ts", "@docs/"]);
    expect(wrapper.get("[data-testid='prompt-references']").text()).toBe("look at @src/app.ts and @docs/ please");

    textarea.element.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
    await flushPromises();

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const promptCall = (mockApi.POST.mock.calls as any[]).find(([url]) => url === "/api/sessions/{id}/prompt");
    expect((promptCall?.[1]?.body as { text?: string }).text).toBe("look at @src/app.ts and @docs/ please");
  });

  it("does not intercept Shift+Enter and does not render autocomplete when sessionId is blank", async () => {
    const wrapper = mountComposer({ sessionId: "   " });
    const textarea = wrapper.get("[data-testid='prompt-input']");

    await textarea.setValue("/");
    await flushPromises();

    expect(wrapper.find(".autocomplete-popup").exists()).toBe(false);

    const shiftEnterEvent = new KeyboardEvent("keydown", {
      key: "Enter",
      shiftKey: true,
      bubbles: true,
      cancelable: true,
    });

    textarea.element.dispatchEvent(shiftEnterEvent);

    expect(shiftEnterEvent.defaultPrevented).toBe(false);
    expect(mockApi.POST).not.toHaveBeenCalled();
  });

  it("enables composer for a stopped session when capabilities canPrompt is true", async () => {
    const wrapper = mountComposer({
      session: createSession({
        lifecycleStatus: "stopped",
        sessionStatus: "stopped",
        capabilities: createCapabilities({ canPrompt: true }),
      }),
    });

    await flushPromises();

    const textarea = wrapper.get("[data-testid='prompt-input']");
    expect(textarea.attributes("disabled")).toBeUndefined();

    // Add content to verify the send button can be enabled
    await textarea.setValue("test message");
    await flushPromises();

    expect(wrapper.get("[data-testid='prompt-send-button']").attributes("disabled")).toBeUndefined();
  });

  it("disables composer for an errored session when capabilities canPrompt is false", async () => {
    const wrapper = mountComposer({
      session: createSession({
        lifecycleStatus: "error",
        sessionStatus: "error",
        capabilities: createCapabilities({
          canPrompt: false,
          promptDisabledReason: "Session is not running.",
        }),
      }),
    });

    await flushPromises();

    expect(wrapper.get("[data-testid='prompt-input']").attributes("disabled")).toBeDefined();
    expect(wrapper.get("[data-testid='prompt-send-button']").attributes("disabled")).toBeDefined();
  });

  it("disables composer for an archived session", async () => {
    const wrapper = mountComposer({
      session: createSession({
        retentionStatus: "archived",
        archivedAt: "2026-05-28T00:00:00Z",
        capabilities: createCapabilities({ canPrompt: true }),
      }),
    });

    await flushPromises();

    expect(wrapper.get("[data-testid='prompt-input']").attributes("disabled")).toBeDefined();
    expect(wrapper.get("[data-testid='prompt-send-button']").attributes("disabled")).toBeDefined();
  });
});

describe("Composer shell commands", () => {
  const harnesses = [
    { type: "opencode", displayName: "OpenCode", available: true, userEnabled: true, capabilities: { supportsShellCommands: true, supportsSteering: true } },
    { type: "claude-code", displayName: "Claude Code", available: true, userEnabled: true, capabilities: { supportsShellCommands: false } },
  ];

  function pressKey(element: Element, key: string): KeyboardEvent {
    const event = new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true });
    element.dispatchEvent(event);
    return event;
  }

  function shellCalls() {
    return (mockApi.POST.mock.calls as unknown as [string, unknown][]).filter(([url]) => url === "/api/sessions/{id}/shell");
  }

  function respondToShell(response: Response, body?: unknown): void {
    const fallback = mockApi.POST.getMockImplementation() as unknown as (url: string, init?: unknown) => Promise<unknown>;
    mockApi.POST.mockImplementation((async (url: string, init?: unknown) => {
      if (url === "/api/sessions/{id}/shell") {
        return { data: undefined, error: body, response };
      }
      return fallback(url, init);
    }) as never);
  }

  beforeEach(() => {
    mockApi.GET.mockReset();
    mockApi.POST.mockReset();
    configureApiFetch();
    const fallback = mockApi.GET.getMockImplementation() as unknown as (url: string, init?: unknown) => Promise<unknown>;
    mockApi.GET.mockImplementation((async (url: string, init?: unknown) => {
      if (url === "/api/harnesses") {
        return { data: harnesses, error: undefined, response: new Response() };
      }
      return fallback(url, init);
    }) as never);
    respondToShell(new Response(null, { status: 202 }));
    useDraftState("session-1", { agentId: "", modelId: "" }).resetText();
  });

  it("runs a draft that starts with ! as a shell command, not a prompt", async () => {
    const wrapper = mountComposer();
    await flushPromises();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    await textarea.setValue("!git status");
    expect(wrapper.find("[data-testid='composer-shell-mode']").exists()).toBe(true);
    expect(wrapper.get("[data-testid='prompt-send-button']").attributes("title")).toBe("Run command");

    const enter = pressKey(textarea.element, "Enter");
    await flushPromises();

    expect(enter.defaultPrevented).toBe(true);
    expect(shellCalls()).toHaveLength(1);
    expect(shellCalls()[0][1]).toEqual(expect.objectContaining({
      params: { path: { id: "session-1" } },
      body: { command: "git status" },
    }));
    expect(mockApi.POST).not.toHaveBeenCalledWith("/api/sessions/{id}/prompt", expect.anything());
    expect(wrapper.emitted("promptSent")).toBeUndefined();
    expect((textarea.element as HTMLTextAreaElement).value).toBe("");
  });

  it("leaves ! as plain text for a harness that can't run shell commands", async () => {
    const wrapper = mountComposer({ session: createSession({ harnessType: "claude-code" }) });
    await flushPromises();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    await textarea.setValue("!ls");
    expect(wrapper.find("[data-testid='composer-shell-mode']").exists()).toBe(false);

    pressKey(textarea.element, "Enter");
    await flushPromises();

    expect(shellCalls()).toHaveLength(0);
    expect(mockApi.POST).toHaveBeenCalledWith("/api/sessions/{id}/prompt", expect.anything());
  });

  it("doesn't send a ! draft as a prompt before it knows whether the harness runs commands", async () => {
    let answerHarnesses: (() => void) | undefined;
    const harnessesAnswered = new Promise<void>((resolve) => { answerHarnesses = resolve; });
    const fallback = mockApi.GET.getMockImplementation() as unknown as (url: string, init?: unknown) => Promise<unknown>;
    mockApi.GET.mockImplementation((async (url: string, init?: unknown) => {
      if (url === "/api/harnesses") await harnessesAnswered;
      return fallback(url, init);
    }) as never);

    const wrapper = mountComposer();
    const textarea = wrapper.get("[data-testid='prompt-input']");
    await textarea.setValue("!git status");
    pressKey(textarea.element, "Enter");
    await flushPromises();

    expect(mockApi.POST).not.toHaveBeenCalledWith("/api/sessions/{id}/prompt", expect.anything());
    expect(wrapper.get("[data-testid='prompt-send-button']").attributes("disabled")).toBeDefined();

    answerHarnesses?.();
    await flushPromises();
    expect(wrapper.find("[data-testid='composer-shell-mode']").exists()).toBe(true);
    pressKey(textarea.element, "Enter");
    await flushPromises();

    expect(shellCalls()).toHaveLength(1);
    expect(mockApi.POST).not.toHaveBeenCalledWith("/api/sessions/{id}/prompt", expect.anything());
  });

  it("goes back to a prompt on Escape, keeping what was typed", async () => {
    const wrapper = mountComposer();
    await flushPromises();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    await textarea.setValue("!npm test");
    const escape = pressKey(textarea.element, "Escape");
    await flushPromises();

    expect(escape.defaultPrevented).toBe(true);
    expect((textarea.element as HTMLTextAreaElement).value).toBe("npm test");
    expect(wrapper.find("[data-testid='composer-shell-mode']").exists()).toBe(false);
  });

  it("offers no @ references or / commands in a shell command", async () => {
    const wrapper = mountComposer();
    await flushPromises();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    await textarea.setValue("!cat @src");
    await flushPromises();

    expect(wrapper.find(".autocomplete-popup__item").exists()).toBe(false);
    expect(wrapper.find(".composer-reference").exists()).toBe(false);
  });

  it("queues a command typed during a turn with Fleet, which runs it when the turn ends", async () => {
    const wrapper = mountComposer({ session: createSession({ activityStatus: "busy" }) });
    await flushPromises();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    await textarea.setValue("!git status");
    // A command runs between turns, so nothing offers to send it into the turn.
    expect(wrapper.find("[data-testid='prompt-send-now-button']").exists()).toBe(false);
    pressKey(textarea.element, "Enter");
    await flushPromises();

    expect(shellCalls()).toHaveLength(0);
    const queued = (mockApi.POST.mock.calls as unknown as [string, { body?: unknown }][]).filter(([url]) => url === "/api/sessions/{id}/queue");
    expect(queued.map(([, init]) => init.body)).toEqual([expect.objectContaining({ text: "!git status", kind: "shell" })]);
    expect(wrapper.findAll("[data-testid='queued-message']").map((item) => item.text())).toEqual([expect.stringContaining("!git status")]);
    expect(wrapper.find("[data-testid='queued-send-now']").exists()).toBe(false);

    // Fleet sends what's queued, not the browser.
    useSessionsStore().patchSession("session-1", { activityStatus: "idle" });
    await flushPromises();
    expect(shellCalls()).toHaveLength(0);
  });

  it("puts a refused command back and says why", async () => {
    respondToShell(new Response(null, { status: 409 }), { error: "The agent is working. Run the command when its turn ends." });
    const wrapper = mountComposer();
    await flushPromises();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    await textarea.setValue("!git status");
    pressKey(textarea.element, "Enter");
    await flushPromises();

    expect((textarea.element as HTMLTextAreaElement).value).toBe("!git status");
    expect(wrapper.get("[data-testid='send-prompt-error']").text()).toBe("The agent is working. Run the command when its turn ends.");
  });
});

describe("Composer side questions (/btw)", () => {
  const harnesses = [
    { type: "opencode", displayName: "OpenCode", available: true, userEnabled: true, capabilities: { supportsSideConversations: true } },
    { type: "claude-code", displayName: "Claude Code", available: true, userEnabled: true, capabilities: { supportsSideConversations: false } },
  ];
  const side = {
    sessionId: "side-1",
    instanceId: "instance-side",
    title: "btw: what changed?",
    boundaryMessageId: "msg_boundary",
    createdAt: "2026-09-25T00:00:00Z",
    minimized: false,
  };

  function pressKey(element: Element, key: string): KeyboardEvent {
    const event = new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true });
    element.dispatchEvent(event);
    return event;
  }

  function sideCalls() {
    return (mockApi.POST.mock.calls as unknown as [string, unknown][]).filter(([url]) => url === "/api/sessions/{id}/side");
  }

  function respondToSide(response: Response, data?: unknown, error?: unknown): void {
    const fallback = mockApi.POST.getMockImplementation() as unknown as (url: string, init?: unknown) => Promise<unknown>;
    mockApi.POST.mockImplementation((async (url: string, init?: unknown) => {
      if (url === "/api/sessions/{id}/side") {
        return { data, error, response };
      }
      return fallback(url, init);
    }) as never);
  }

  function withOpenSideConversation(): void {
    const fallback = mockApi.GET.getMockImplementation() as unknown as (url: string, init?: unknown) => Promise<unknown>;
    mockApi.GET.mockImplementation((async (url: string, init?: unknown) => {
      if (url === "/api/sessions/{id}/side") {
        return { data: side, error: undefined, response: new Response(null, { status: 200 }) };
      }
      return fallback(url, init);
    }) as never);
  }

  beforeEach(() => {
    _resetSideConversationsForTesting();
    mockApi.GET.mockReset();
    mockApi.POST.mockReset();
    configureApiFetch();
    const fallback = mockApi.GET.getMockImplementation() as unknown as (url: string, init?: unknown) => Promise<unknown>;
    mockApi.GET.mockImplementation((async (url: string, init?: unknown) => {
      if (url === "/api/harnesses") {
        return { data: harnesses, error: undefined, response: new Response() };
      }
      if (url === "/api/sessions/{id}/side") {
        return { data: undefined, error: undefined, response: new Response(null, { status: 204 }) };
      }
      return fallback(url, init);
    }) as never);
    respondToSide(new Response(), { sideConversation: side, correlationId: "c-1", messageId: "msg_question" });
    useDraftState("session-1", { agentId: "", modelId: "" }).resetText();
  });

  it("asks /btw <question> in the side conversation, not the session, even while the session works", async () => {
    const wrapper = mountComposer({ session: createSession({ activityStatus: "busy" }) });
    await flushPromises();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    await textarea.setValue("/btw what changed?");
    pressKey(textarea.element, "Enter");
    await flushPromises();

    expect(sideCalls()).toHaveLength(1);
    expect(sideCalls()[0][1]).toEqual(expect.objectContaining({
      params: { path: { id: "session-1" } },
      body: expect.objectContaining({ text: "what changed?" }),
    }));
    expect(mockApi.POST).not.toHaveBeenCalledWith("/api/sessions/{id}/prompt", expect.anything());
    expect(mockApi.POST).not.toHaveBeenCalledWith("/api/sessions/{id}/command", expect.anything());
    expect(wrapper.find("[data-testid='queued-message']").exists()).toBe(false);
    expect((textarea.element as HTMLTextAreaElement).value).toBe("");
    // The composer now talks to the side conversation.
    expect(wrapper.find("[data-testid='composer-side-mode']").exists()).toBe(true);
  });

  it("sends what's typed to the open side conversation", async () => {
    withOpenSideConversation();
    const wrapper = mountComposer();
    await flushPromises();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    expect(wrapper.find("[data-testid='composer-side-mode']").exists()).toBe(true);
    expect(textarea.attributes("placeholder")).toBe("Ask a follow-up in the side conversation…");

    await textarea.setValue("and the tests?");
    pressKey(textarea.element, "Enter");
    await flushPromises();

    expect(sideCalls()).toHaveLength(1);
    expect(sideCalls()[0][1]).toEqual(expect.objectContaining({ body: expect.objectContaining({ text: "and the tests?" }) }));
    expect(mockApi.POST).not.toHaveBeenCalledWith("/api/sessions/{id}/prompt", expect.anything());
  });

  it("asks for a question after a bare /btw", async () => {
    const wrapper = mountComposer();
    await flushPromises();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    await textarea.setValue("/btw");
    await wrapper.get("[data-testid='prompt-send-button']").trigger("click");
    await flushPromises();

    expect(sideCalls()).toHaveLength(0);
    expect(wrapper.get("[data-testid='send-prompt-error']").text()).toBe("Type a question after /btw.");
  });

  it("puts a refused question back and says why", async () => {
    respondToSide(new Response(null, { status: 400 }), undefined, { error: "Claude Code sessions can't fork, so /btw isn't available here." });
    const wrapper = mountComposer({ session: createSession({ harnessType: "claude-code" }) });
    await flushPromises();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    await textarea.setValue("/btw what changed?");
    pressKey(textarea.element, "Enter");
    await flushPromises();

    expect((textarea.element as HTMLTextAreaElement).value).toBe("/btw what changed?");
    expect(wrapper.get("[data-testid='send-prompt-error']").text()).toBe("Claude Code sessions can't fork, so /btw isn't available here.");
    expect(wrapper.find("[data-testid='composer-side-mode']").exists()).toBe(false);
    expect(mockApi.POST).not.toHaveBeenCalledWith("/api/sessions/{id}/command", expect.anything());
  });

  it("lists /btw in the / popup only for a harness that can fork", async () => {
    const wrapper = mountComposer();
    await flushPromises();
    const textarea = wrapper.get("[data-testid='prompt-input']");
    await textarea.setValue("/");
    (textarea.element as HTMLTextAreaElement).setSelectionRange(1, 1);
    await textarea.trigger("keyup");
    await flushPromises();

    const labels = wrapper.findAll(".autocomplete-popup__item").map((item) => item.text());
    expect(labels[0]).toContain("/btw");
    expect(labels[0]).toContain("Ask a side question without disturbing the session");

    wrapper.unmount();
    const other = mountComposer({ session: createSession({ harnessType: "claude-code" }) });
    await flushPromises();
    const otherTextarea = other.get("[data-testid='prompt-input']");
    await otherTextarea.setValue("/");
    (otherTextarea.element as HTMLTextAreaElement).setSelectionRange(1, 1);
    await otherTextarea.trigger("keyup");
    await flushPromises();

    expect(other.findAll(".autocomplete-popup__item").map((item) => item.text()).join(" ")).not.toContain("/btw");
  });
});

describe("Composer with a minimized side conversation", () => {
  const harnesses = [
    { type: "opencode", displayName: "OpenCode", available: true, userEnabled: true, capabilities: { supportsSideConversations: true } },
  ];
  const side = {
    sessionId: "side-1",
    instanceId: "instance-side",
    title: "btw: what changed?",
    boundaryMessageId: "msg_boundary",
    createdAt: "2026-09-25T00:00:00Z",
    minimized: true,
  };

  function pressKey(element: Element, key: string): void {
    element.dispatchEvent(new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true }));
  }

  beforeEach(() => {
    _resetSideConversationsForTesting();
    mockApi.GET.mockReset();
    mockApi.POST.mockReset();
    mockApi.PUT.mockReset();
    configureApiFetch();
    const fallback = mockApi.GET.getMockImplementation() as unknown as (url: string, init?: unknown) => Promise<unknown>;
    mockApi.GET.mockImplementation((async (url: string, init?: unknown) => {
      if (url === "/api/harnesses") return { data: harnesses, error: undefined, response: new Response() };
      if (url === "/api/sessions/{id}/side") return { data: side, error: undefined, response: new Response(null, { status: 200 }) };
      return fallback(url, init);
    }) as never);
    mockApi.PUT.mockImplementation((async (_url: string, init: { body: { minimized: boolean } }) =>
      ({ data: { ...side, minimized: init.body.minimized }, error: undefined, response: new Response() })) as never);
    useDraftState("session-side", { agentId: "", modelId: "" }).resetText();
  });

  it("sends to the session, looking like the plain composer", async () => {
    const wrapper = mountComposer({ sessionId: "session-side" });
    await flushPromises();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    expect(wrapper.find("[data-testid='composer-side-mode']").exists()).toBe(false);
    expect(textarea.attributes("placeholder")).toBe("Type a message…");

    await textarea.setValue("carry on");
    pressKey(textarea.element, "Enter");
    await flushPromises();

    expect(mockApi.POST).toHaveBeenCalledWith("/api/sessions/{id}/prompt", expect.anything());
    expect(mockApi.POST).not.toHaveBeenCalledWith("/api/sessions/{id}/side", expect.anything());
    wrapper.unmount();
  });

  it("keeps each side's draft when the side conversation opens and folds", async () => {
    const wrapper = mountComposer({ sessionId: "session-side" });
    await flushPromises();
    const textarea = wrapper.get("[data-testid='prompt-input']");
    const { setMinimized } = useSideConversation("session-side");

    await textarea.setValue("for the session");
    await setMinimized(false);
    await flushPromises();
    expect(wrapper.find("[data-testid='composer-side-mode']").exists()).toBe(true);
    expect((textarea.element as HTMLTextAreaElement).value).toBe("");

    await textarea.setValue("for the side");
    await setMinimized(true);
    await flushPromises();
    expect((textarea.element as HTMLTextAreaElement).value).toBe("for the session");

    await setMinimized(false);
    await flushPromises();
    expect((textarea.element as HTMLTextAreaElement).value).toBe("for the side");
  });
});

describe("Composer steering", () => {
  const harnesses = [
    { type: "opencode", displayName: "OpenCode", available: true, userEnabled: true, capabilities: { supportsSteering: true } },
    { type: "claude-code", displayName: "Claude Code", available: true, userEnabled: true, capabilities: { supportsSteering: false } },
  ];

  function pressKey(element: Element, key: string, init: KeyboardEventInit = {}): KeyboardEvent {
    const event = new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true, ...init });
    element.dispatchEvent(event);
    return event;
  }

  function bodiesFor(path: string): Record<string, unknown>[] {
    return (mockApi.POST.mock.calls as unknown as [string, { body?: Record<string, unknown> }][])
      .filter(([url]) => url === path)
      .map(([, init]) => init.body ?? {});
  }

  const promptBodies = () => bodiesFor("/api/sessions/{id}/prompt");
  const queuedBodies = () => bodiesFor("/api/sessions/{id}/queue");

  function sendNowCalls(): unknown[] {
    return (mockApi.POST.mock.calls as unknown as [string, { params?: unknown }][])
      .filter(([url]) => url === "/api/sessions/{id}/queue/{itemId}/send")
      .map(([, init]) => init.params);
  }

  beforeEach(() => {
    mockApi.GET.mockReset();
    mockApi.POST.mockReset();
    configureApiFetch();
    const fallback = mockApi.GET.getMockImplementation() as unknown as (url: string, init?: unknown) => Promise<unknown>;
    mockApi.GET.mockImplementation((async (url: string, init?: unknown) => {
      if (url === "/api/harnesses") {
        return { data: harnesses, error: undefined, response: new Response() };
      }
      return fallback(url, init);
    }) as never);
    useDraftState("session-1", { agentId: "", modelId: "" }).resetText();
  });

  it("queues a message on Enter while the agent works, and sends it into the turn on Ctrl+Enter", async () => {
    const wrapper = mountComposer({ session: createSession({ activityStatus: "busy" }) });
    await flushPromises();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    await textarea.setValue("also check the tests");
    expect(wrapper.find("[data-testid='prompt-send-now-button']").exists()).toBe(true);
    pressKey(textarea.element, "Enter");
    await flushPromises();

    expect(promptBodies()).toHaveLength(0);
    expect(queuedBodies()).toEqual([expect.objectContaining({ text: "also check the tests", kind: "prompt" })]);
    expect(wrapper.findAll("[data-testid='queued-message']")).toHaveLength(1);

    await textarea.setValue("stop, that's the wrong file");
    pressKey(textarea.element, "Enter", { ctrlKey: true });
    await flushPromises();

    expect(promptBodies()).toEqual([expect.objectContaining({ text: "stop, that's the wrong file", delivery: "steer" })]);
    expect(wrapper.findAll("[data-testid='queued-message']")).toHaveLength(1);
  });

  it("sends a queued message into the turn with Send now, leaving the draft alone", async () => {
    const wrapper = mountComposer({ session: createSession({ activityStatus: "busy" }) });
    await flushPromises();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    await textarea.setValue("use the other branch");
    pressKey(textarea.element, "Enter");
    await flushPromises();
    await textarea.setValue("still typing");

    await wrapper.get("[data-testid='queued-send-now']").trigger("click");
    await flushPromises();

    // Fleet takes it out of the queue and steers it in.
    expect(sendNowCalls()).toEqual([{ path: { id: "session-1", itemId: expect.stringMatching(/^queued-/) } }]);
    expect(promptBodies()).toHaveLength(0);
    expect(wrapper.find("[data-testid='queued-message']").exists()).toBe(false);
    expect((textarea.element as HTMLTextAreaElement).value).toBe("still typing");
  });

  it("offers only the queue for a harness that can't steer", async () => {
    const wrapper = mountComposer({ session: createSession({ activityStatus: "busy", harnessType: "claude-code" }) });
    await flushPromises();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    await textarea.setValue("also check the tests");
    expect(wrapper.find("[data-testid='prompt-send-now-button']").exists()).toBe(false);
    pressKey(textarea.element, "Enter", { ctrlKey: true });
    await flushPromises();

    expect(promptBodies()).toHaveLength(0);
    expect(wrapper.findAll("[data-testid='queued-message']")).toHaveLength(1);
    expect(wrapper.find("[data-testid='queued-send-now']").exists()).toBe(false);
  });

  it("offers no Send now for a side question, which never waits for the turn", async () => {
    const wrapper = mountComposer({ session: createSession({ activityStatus: "busy" }) });
    await flushPromises();

    await wrapper.get("[data-testid='prompt-input']").setValue("/btw what does this file do?");

    expect(wrapper.find("[data-testid='prompt-send-now-button']").exists()).toBe(false);
  });

  it("queues a slash command as a command, to run when the turn ends", async () => {
    const wrapper = mountComposer({ session: createSession({ activityStatus: "busy" }) });
    await flushPromises();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    await textarea.setValue("/review src/auth");
    pressKey(textarea.element, "Enter", { ctrlKey: true });
    await flushPromises();

    expect(promptBodies()).toHaveLength(0);
    expect(queuedBodies()).toEqual([expect.objectContaining({ kind: "command", command: "review", arguments: "src/auth" })]);
    expect(wrapper.find("[data-testid='queued-send-now']").exists()).toBe(false);
  });

  it("shows what Fleet has queued when the session opens again", async () => {
    const fallback = mockApi.GET.getMockImplementation() as unknown as (url: string, init?: unknown) => Promise<unknown>;
    mockApi.GET.mockImplementation((async (url: string, init?: unknown) => {
      if (url === "/api/sessions/{id}/queue") {
        return {
          data: [{ id: "queued-kept", kind: "prompt", text: "queued before you left", createdAt: "2026-09-25T10:00:00Z" }],
          error: undefined,
          response: new Response(),
        };
      }
      return fallback(url, init);
    }) as never);

    const wrapper = mountComposer({ session: createSession({ activityStatus: "busy" }) });
    await flushPromises();

    expect(wrapper.findAll("[data-testid='queued-message']").map((item) => item.text())).toEqual([expect.stringContaining("queued before you left")]);
  });

  it("follows the queue Fleet announces: sent, removed or queued from another tab", async () => {
    const wrapper = mountComposer({ session: createSession({ activityStatus: "busy" }) });
    await flushPromises();

    pushSessionEvent("session-1", {
      type: "session.queue",
      payload: { sessionId: "session-1", items: [{ id: "q1", kind: "prompt", text: "from the other tab", createdAt: "" }] },
    });
    await flushPromises();
    expect(wrapper.findAll("[data-testid='queued-message']").map((item) => item.text())).toEqual([expect.stringContaining("from the other tab")]);

    // Another session's queue isn't this one's.
    pushSessionEvent("session-1", { type: "session.queue", payload: { sessionId: "session-2", items: [] } });
    await flushPromises();
    expect(wrapper.findAll("[data-testid='queued-message']")).toHaveLength(1);

    pushSessionEvent("session-1", { type: "session.queue", payload: { sessionId: "session-1", items: [] } });
    await flushPromises();
    expect(wrapper.find("[data-testid='queued-message']").exists()).toBe(false);
  });

  it("sends the normal way when the agent is idle, even on Ctrl+Enter", async () => {
    const wrapper = mountComposer();
    await flushPromises();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    expect(wrapper.find("[data-testid='prompt-send-now-button']").exists()).toBe(false);
    await textarea.setValue("hello");
    pressKey(textarea.element, "Enter", { ctrlKey: true });
    await flushPromises();

    expect(promptBodies()).toHaveLength(1);
    expect(promptBodies()[0].delivery).toBeUndefined();
  });
});
