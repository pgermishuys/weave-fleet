import { flushPromises } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import { useHarnesses } from "@/composables/use-harnesses";
import type { HarnessInfo } from "@/api/client";
import { mountComposable } from "./test-utils";

const { apiFetchMock } = vi.hoisted(() => ({
  apiFetchMock: vi.fn(),
}));

vi.mock("@/api/client", () => ({
  api: { GET: apiFetchMock },
}));

function harness(version: string): HarnessInfo {
  return {
    type: "opencode",
    displayName: "OpenCode",
    available: true,
    userEnabled: true,
    state: "ready",
    version,
    capabilities: {
      requiresInitialPrompt: false,
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

function reply(harnesses: HarnessInfo[]) {
  return { data: harnesses, error: undefined, response: new Response("[]", { status: 200 }) };
}

describe("useHarnesses", () => {
  it("keeps the newest answer when an older, slower one arrives after it", async () => {
    let answerFirst: (value: ReturnType<typeof reply>) => void = () => {};
    apiFetchMock
      .mockImplementationOnce(() => new Promise((resolve) => (answerFirst = resolve)))
      .mockImplementationOnce(() => Promise.resolve(reply([harness("1.18.31")])));

    const { result } = await mountComposable(() => useHarnesses());
    await result.refresh();
    answerFirst(reply([harness("1.15.10")]));
    await flushPromises();

    expect(result.harnesses.value.map((each) => each.version)).toEqual(["1.18.31"]);
    expect(result.isLoading.value).toBe(false);
  });
});
