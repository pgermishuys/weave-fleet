import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { useSessionsStore } from "@/stores/sessions";
import Composer from "@/components/session/Composer.vue";
import type { SessionListItem } from "@/api/client";
import { createModelSelectionKey } from "@/composables/use-models";
import { addDraftTerminalContext, clearDraftTerminalContext } from "@/composables/use-draft-terminal-context";

vi.mock("@/api/client", () => ({
  api: {
    GET: vi.fn(),
    POST: vi.fn(),
    PUT: vi.fn(),
    DELETE: vi.fn(),
    PATCH: vi.fn(),
  },
}));

const { sessionsTopicHandlers } = vi.hoisted(() => ({ sessionsTopicHandlers: new Set<(event: unknown) => void>() }));
vi.mock("@/composables/use-signalr-socket", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/composables/use-signalr-socket")>()),
  onGlobalEvent: (topic: string, handler: (event: unknown) => void) => {
    if (topic !== "sessions") return () => {};
    sessionsTopicHandlers.add(handler);
    return () => sessionsTopicHandlers.delete(handler);
  },
}));

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

    throw new Error(`Unhandled GET call: ${url}`);
  });

  mockApi.POST.mockImplementation(async (url: string) => {
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
    { type: "opencode", displayName: "OpenCode", available: true, userEnabled: true, capabilities: { supportsShellCommands: true } },
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

  it("holds a command typed during a turn until the turn ends", async () => {
    const wrapper = mountComposer({ session: createSession({ activityStatus: "busy" }) });
    await flushPromises();
    const textarea = wrapper.get("[data-testid='prompt-input']");

    await textarea.setValue("!git status");
    pressKey(textarea.element, "Enter");
    await flushPromises();

    expect(shellCalls()).toHaveLength(0);
    expect(wrapper.get(".queue-badge").text()).toContain("1 queued");

    useSessionsStore().patchSession("session-1", { activityStatus: "idle" });
    await flushPromises();

    expect(shellCalls()).toHaveLength(1);
    expect(shellCalls()[0][1]).toEqual(expect.objectContaining({ body: { command: "git status" } }));
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
