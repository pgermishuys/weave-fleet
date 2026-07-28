import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it, vi } from "vitest";
import HarnessesSection from "@/components/settings/HarnessesSection.vue";
import type { HarnessInfo } from "@/lib/api-types";

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

function createHarness(type: string, displayName: string): HarnessInfo {
  return {
    type,
    displayName,
    available: true,
    userEnabled: true,
    capabilities: {
      requiresInitialPrompt: true,
      supportsAgents: true,
      supportsModelSelection: true,
      supportsCommands: true,
      supportsForking: true,
      supportsResume: true,
      supportsImageAttachments: true,
      supportsStreaming: true,
      supportsDelegation: true,
    },
  };
}

function mockApiResponses(preferences: Record<string, string>): void {
  apiFetchMock.mockImplementation((path: string, init?: RequestInit) => {
    if (path === "/api/harnesses") {
      return Promise.resolve(createJsonResponse([createHarness("opencode", "OpenCode")]));
    }

    if (path === "/api/preferences") {
      return Promise.resolve(createJsonResponse(preferences));
    }

    if (path === "/api/preferences/PooledOpenCodeHarness" && init?.method === "PUT") {
      return Promise.resolve(new Response(null, { status: 204 }));
    }

    return Promise.resolve(createJsonResponse({ error: "unexpected path" }, 404));
  });
}

async function mountHarnessesSection(): Promise<ReturnType<typeof mount>> {
  const wrapper = mount(HarnessesSection, {
    global: {
      plugins: [createPinia()],
    },
  });

  await flushPromises();
  return wrapper;
}

describe("HarnessesSection", () => {
  beforeEach(() => {
    apiFetchMock.mockReset();
    setActivePinia(createPinia());
  });

  it("renders pooled opencode mode off by default", async () => {
    mockApiResponses({});

    const wrapper = await mountHarnessesSection();

    expect(wrapper.text()).toContain("Pooled OpenCode Mode");
    expect(wrapper.text()).toContain("Off by default");
    expect(wrapper.get("button[aria-label='Enable Pooled OpenCode Mode']").attributes("aria-checked")).toBe("false");
  });

  it("persists pooled opencode mode changes", async () => {
    mockApiResponses({});

    const wrapper = await mountHarnessesSection();
    await wrapper.get("button[aria-label='Enable Pooled OpenCode Mode']").trigger("click");

    expect(apiFetchMock).toHaveBeenCalledWith("/api/preferences/PooledOpenCodeHarness", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ value: "true" }),
    });
  });
});
