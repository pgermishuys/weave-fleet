import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";
import type { HarnessInfo } from "@/lib/api-types";
import { usePreferencesStore } from "@/stores/preferences";
import { mountComposable } from "./test-utils";

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

function createHarness(type: string, overrides: Partial<HarnessInfo> = {}): HarnessInfo {
  return {
    type,
    displayName: type,
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
    ...overrides,
  };
}

function mockApiResponses(harnesses: HarnessInfo[], preferences: Record<string, string> = {}): void {
  apiFetchMock.mockImplementation((path: string) => {
    if (path === "/api/harnesses") {
      return Promise.resolve(createJsonResponse(harnesses));
    }

    if (path === "/api/preferences") {
      return Promise.resolve(createJsonResponse(preferences));
    }

    return Promise.resolve(createJsonResponse({ error: "unexpected path" }, 404));
  });
}

describe("useEnabledHarnesses", () => {
  beforeEach(() => {
    apiFetchMock.mockReset();
    setActivePinia(createPinia());
  });

  it("returns only available harnesses enabled by the user", async () => {
    mockApiResponses([
      createHarness("opencode"),
      createHarness("claude-code", { userEnabled: false }),
      createHarness("codex", { available: false }),
    ]);

    const { result } = await mountComposable(() => useEnabledHarnesses());

    expect(result.enabledHarnesses.value.map((harness) => harness.type)).toEqual(["opencode"]);
  });

  it("falls back to opencode when no preference is stored", async () => {
    mockApiResponses([]);

    const { result } = await mountComposable(() => useEnabledHarnesses());

    expect(result.defaultHarnessType.value).toBe("opencode");
  });

  it("reads defaultHarnessType from the preferences store", async () => {
    mockApiResponses([]);
    const preferencesStore = usePreferencesStore();
    preferencesStore.preferences = { defaultHarnessType: "claude-code" };
    preferencesStore.hasFetched = true;

    const { result } = await mountComposable(() => useEnabledHarnesses());

    expect(result.defaultHarnessType.value).toBe("claude-code");
  });
});
