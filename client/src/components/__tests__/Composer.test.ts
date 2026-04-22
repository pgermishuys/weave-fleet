import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { useSessionsStore } from "@/stores/sessions";
import Composer from "@/components/session/Composer.vue";
import type { SessionListItem } from "@/lib/api-types";
import { createModelSelectionKey } from "@/composables/use-models";

const { apiFetchMock } = vi.hoisted(() => ({
  apiFetchMock: vi.fn(),
}));

vi.mock("@/lib/api-client", () => ({
  apiFetch: apiFetchMock,
}));

function createJsonResponse<T>(body: T, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
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
    ...overrides,
  };
}

function configureApiFetch(): void {
  apiFetchMock.mockImplementation(async (url: string, options?: RequestInit) => {
    if (url.endsWith("/agents")) {
      return createJsonResponse([
        { name: "alpha", description: "Planner", mode: "primary", color: "#ff00aa" },
      ]);
    }

    if (url.endsWith("/models")) {
      return createJsonResponse({
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
      });
    }

    if (url.endsWith("/commands")) {
      return createJsonResponse({
        commands: [
          { name: "help", description: "Show help" },
          { name: "status", description: "Show status" },
        ],
      });
    }

    if (url.includes("/find/files?q=")) {
      return createJsonResponse({
        instanceId: "instance-1",
        files: ["src/main.ts"],
      });
    }

    if (url.endsWith("/prompt") && options?.method === "POST") {
      return createJsonResponse({}, 200);
    }

    if (url.endsWith("/command") && options?.method === "POST") {
      return createJsonResponse({}, 202);
    }

    throw new Error(`Unhandled apiFetch call: ${url}`);
  });
}

function mountComposer(instanceId: string | undefined = "instance-1") {
  const sessionsStore = useSessionsStore();
  sessionsStore.setSessions([createSession()]);
  sessionsStore.setActiveSessionId("session-1");

  return mount(Composer, {
    attachTo: document.body,
    props: {
      sessionId: "session-1",
      instanceId,
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
            handleChange(event) {
              this.$emit("update:modelValue", event.target.value);
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
    apiFetchMock.mockReset();
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
    expect(apiFetchMock.mock.calls.some(([url, options]) => String(url).endsWith("/prompt") && options?.method === "POST")).toBe(true);
    expect(wrapper.emitted("promptSent")).toHaveLength(1);
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
    expect(apiFetchMock.mock.calls.some(([url, options]) => String(url).endsWith("/command") && options?.method === "POST")).toBe(true);
    expect(apiFetchMock.mock.calls.some(([url, options]) => String(url).endsWith("/prompt") && options?.method === "POST")).toBe(false);
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

    const promptCall = apiFetchMock.mock.calls.find(([url, options]) => String(url).endsWith("/prompt") && options?.method === "POST");
    expect(promptCall).toBeTruthy();
    const [, options] = promptCall!;
    const body = JSON.parse(String(options?.body)) as { model?: { providerID: string; modelID: string } };
    expect(body.model).toEqual({ providerID: "provider-2", modelID: "shared-model" });
  });

  it("does not intercept Shift+Enter and does not render autocomplete when instanceId is blank", async () => {
    const wrapper = mountComposer("   ");
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
    expect(apiFetchMock.mock.calls.some(([url]) => String(url).includes("/prompt"))).toBe(false);
  });
});
