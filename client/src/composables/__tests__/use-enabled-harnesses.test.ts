import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";
import type { HarnessInfo } from "@/api/client";
import { usePreferencesStore } from "@/stores/preferences";
import { mountComposable } from "./test-utils";

const { apiFetchMock } = vi.hoisted(() => ({
  apiFetchMock: vi.fn(),
}));

vi.mock("@/api/client", () => ({
  api: {
    GET: apiFetchMock,
    POST: vi.fn(),
    PUT: vi.fn(),
    DELETE: vi.fn(),
    PATCH: vi.fn(),
  },
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
    state: "ready",
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
      return Promise.resolve({
        data: harnesses,
        error: undefined,
        response: createJsonResponse(harnesses),
      });
    }

    if (path === "/api/preferences") {
      return Promise.resolve({
        data: preferences,
        error: undefined,
        response: createJsonResponse(preferences),
      });
    }

    return Promise.resolve({
      data: undefined,
      error: { message: "unexpected path" },
      response: createJsonResponse({ error: "unexpected path" }, 404),
    });
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

  it("says why the default harness can't start a session when none is ready", async () => {
    mockApiResponses([
      createHarness("claude-code", { available: false, state: "sign-in-required", reason: "Claude Code isn't signed in." }),
      createHarness("opencode", {
        available: false,
        state: "not-installed",
        reason: "OpenCode isn't installed: Fleet couldn't find opencode on PATH or in the folders its installer uses.",
      }),
    ]);

    const { result } = await mountComposable(() => useEnabledHarnesses());

    expect(result.noHarnessReason.value).toBe(
      "OpenCode isn't installed: Fleet couldn't find opencode on PATH or in the folders its installer uses.",
    );
  });

  it("says every harness is turned off when the user turned them all off", async () => {
    mockApiResponses([createHarness("opencode", { userEnabled: false })]);

    const { result } = await mountComposable(() => useEnabledHarnesses());

    expect(result.noHarnessReason.value).toBe("Every harness is turned off.");
  });

  it("has no reason when a harness is ready, or when the list couldn't load", async () => {
    mockApiResponses([createHarness("opencode")]);
    const ready = await mountComposable(() => useEnabledHarnesses());
    expect(ready.result.noHarnessReason.value).toBeNull();

    apiFetchMock.mockReset();
    apiFetchMock.mockImplementation((path: string) => Promise.resolve(path === "/api/harnesses"
      ? { data: undefined, error: { error: "boom" }, response: createJsonResponse({ error: "boom" }, 500) }
      : { data: {}, error: undefined, response: createJsonResponse({}) }));
    const failed = await mountComposable(() => useEnabledHarnesses());
    expect(failed.result.noHarnessReason.value).toBeNull();
  });
});
