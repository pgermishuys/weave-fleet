import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it, vi } from "vitest";
import HarnessesSection from "@/components/settings/HarnessesSection.vue";
import type { HarnessInfo } from "@/api/client";

const { apiFetchMock } = vi.hoisted(() => ({
  apiFetchMock: vi.fn(),
}));

vi.mock("@/api/client", () => ({
  api: {
    GET: apiFetchMock,
    POST: apiFetchMock,
    PUT: apiFetchMock,
    DELETE: apiFetchMock,
  },
}));

function reply<T>(body: T, status = 200) {
  const ok = status >= 200 && status < 300;
  return Promise.resolve({
    data: ok ? body : undefined,
    error: ok ? undefined : body,
    response: createJsonResponse(body, status),
  });
}

function createJsonResponse<T>(body: T, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function createHarness(type: string, displayName: string, overrides: Partial<HarnessInfo> = {}): HarnessInfo {
  return {
    type,
    displayName,
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

function mockApiResponses(
  preferences: Record<string, string>,
  harnesses: () => HarnessInfo[] = () => [createHarness("opencode", "OpenCode")],
): void {
  apiFetchMock.mockImplementation((path: string) => {
    if (path === "/api/harnesses") {
      return reply(harnesses());
    }

    if (path === "/api/preferences") {
      return reply(preferences);
    }

    if (path === "/api/preferences/{key}") {
      return reply(null);
    }

    return reply({ error: "unexpected path" }, 404);
  });
}

async function mountHarnessesSection() {
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

  it("renders harnesses section", async () => {
    mockApiResponses({});

    const wrapper = await mountHarnessesSection();

    expect(wrapper.text()).toContain("Harnesses");
  });

  it("says a harness isn't installed, and why", async () => {
    mockApiResponses({}, () => [createHarness("opencode", "OpenCode", {
      available: false,
      state: "not-installed",
      reason: "OpenCode isn't installed: Fleet couldn't find opencode on PATH or in the folders its installer uses.",
    })]);

    const wrapper = await mountHarnessesSection();

    expect(wrapper.text()).toContain("Not installed");
    expect(wrapper.text()).toContain("Fleet couldn't find opencode on PATH");
    expect(wrapper.find("[data-testid='harness-location']").exists()).toBe(false);
  });

  it("shows the version and where Fleet found a harness that needs a sign-in", async () => {
    mockApiResponses({ "claude-code.enabled": "true" }, () => [createHarness("claude-code", "Claude Code", {
      available: false,
      state: "sign-in-required",
      reason: "Claude Code isn't signed in. Run claude auth login.",
      version: "2.1.276",
      executablePath: "/home/you/.local/bin/claude",
    })]);

    const wrapper = await mountHarnessesSection();

    expect(wrapper.text()).toContain("Sign-in needed");
    expect(wrapper.get("[data-testid='harness-location']").text()).toBe("2.1.276 · /home/you/.local/bin/claude");
  });

  it("checks again, so a harness installed a moment ago shows up", async () => {
    let installed = false;
    mockApiResponses({}, () => [installed
      ? createHarness("opencode", "OpenCode", { version: "1.18.30", executablePath: "/home/you/.opencode/bin/opencode" })
      : createHarness("opencode", "OpenCode", { available: false, state: "not-installed", reason: "OpenCode isn't installed." })]);
    const wrapper = await mountHarnessesSection();
    expect(wrapper.text()).toContain("Not installed");

    installed = true;
    await wrapper.get("[data-testid='harnesses-check-again']").trigger("click");
    await flushPromises();

    expect(wrapper.text()).toContain("Ready");
    expect(wrapper.get("[data-testid='harness-location']").text()).toBe("1.18.30 · /home/you/.opencode/bin/opencode");
  });
});
