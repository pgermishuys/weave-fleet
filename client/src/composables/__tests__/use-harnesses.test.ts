import { flushPromises } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { forgetHarnessLists, refreshAllHarnesses, useHarnesses } from "@/composables/use-harnesses";
import type { HarnessInfo } from "@/api/client";
import { MACHINE_TARGET, type MachineTarget } from "@/lib/machine-target";
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
  beforeEach(() => {
    forgetHarnessLists();
    window.localStorage.clear();
    apiFetchMock.mockReset();
  });

  it("asks once for every list mounted together, as a page with a row per session does", async () => {
    apiFetchMock.mockReset();
    apiFetchMock.mockImplementation(() => Promise.resolve(reply([harness("1.18.31")])));

    const lists = await Promise.all([1, 2, 3].map(() => mountComposable(() => useHarnesses())));
    await flushPromises();

    expect(apiFetchMock).toHaveBeenCalledTimes(1);
    for (const { result } of lists) {
      expect(result.harnesses.value.map((each) => each.version)).toEqual(["1.18.31"]);
      expect(result.isLoading.value).toBe(false);
    }
  });

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

  it("draws the list saved on the last visit at once, and asks the machine behind it", async () => {
    apiFetchMock.mockImplementation(() => Promise.resolve(reply([harness("1.18.31")])));
    await mountComposable(() => useHarnesses());
    expect(JSON.parse(window.localStorage.getItem("weave:saved:harnesses:home")!).harnesses[0].version).toBe("1.18.31");

    // A reload: nothing on the page, the saved list in the browser.
    forgetHarnessLists();
    let answer: (value: ReturnType<typeof reply>) => void = () => {};
    apiFetchMock.mockImplementation(() => new Promise((resolve) => (answer = resolve)));
    const { result } = await mountComposable(() => useHarnesses());

    expect(result.harnesses.value.map((each) => each.version)).toEqual(["1.18.31"]);
    expect(result.isLoading.value).toBe(true);

    answer(reply([harness("1.18.32")]));
    await flushPromises();
    expect(result.harnesses.value.map((each) => each.version)).toEqual(["1.18.32"]);
    expect(result.isLoading.value).toBe(false);
  });

  it("uses a moment-old answer without asking again, as rows mounted one after another do", async () => {
    apiFetchMock.mockImplementation(() => Promise.resolve(reply([harness("1.18.31")])));
    await mountComposable(() => useHarnesses());
    const { result } = await mountComposable(() => useHarnesses());

    expect(apiFetchMock).toHaveBeenCalledTimes(1);
    expect(result.harnesses.value.map((each) => each.version)).toEqual(["1.18.31"]);
  });

  it("asks the machine to check every harness again for Check again and after a change", async () => {
    apiFetchMock.mockImplementation(() => Promise.resolve(reply([harness("1.18.31")])));
    const { result } = await mountComposable(() => useHarnesses());
    expect(apiFetchMock).toHaveBeenLastCalledWith("/api/harnesses", {});

    await result.refresh();
    expect(apiFetchMock).toHaveBeenLastCalledWith("/api/harnesses", { params: { query: { fresh: true } } });

    apiFetchMock.mockImplementation(() => Promise.resolve(reply([harness("1.18.32")])));
    refreshAllHarnesses();
    await flushPromises();
    expect(apiFetchMock).toHaveBeenCalledTimes(3);
    expect(apiFetchMock).toHaveBeenLastCalledWith("/api/harnesses", { params: { query: { fresh: true } } });
    expect(result.harnesses.value.map((each) => each.version)).toEqual(["1.18.32"]);
  });

  it("keeps each machine's list apart", async () => {
    window.localStorage.setItem("weave:saved:harnesses:m-mac", JSON.stringify({ harnesses: [harness("0.9.0")], savedAt: 1 }));
    apiFetchMock.mockImplementation(() => Promise.resolve(reply([harness("1.18.31")])));
    await mountComposable(() => useHarnesses());

    expect(JSON.parse(window.localStorage.getItem("weave:saved:harnesses:m-mac")!).harnesses[0].version).toBe("0.9.0");
    expect(JSON.parse(window.localStorage.getItem("weave:saved:harnesses:home")!).harnesses[0].version).toBe("1.18.31");
  });

  it("asks the machine the page provides, and saves its list under that machine", async () => {
    apiFetchMock.mockImplementation(() => Promise.resolve(reply([harness("1.18.31")])));
    const macGet = vi.fn(() => Promise.resolve(reply([harness("2.0.18")])));
    const mac = { key: "m-mac", connection: null, isLive: false, api: { GET: macGet } } as unknown as MachineTarget;

    const { result } = await mountComposable(() => useHarnesses(), { provide: { [MACHINE_TARGET]: () => mac } });

    expect(macGet).toHaveBeenCalledWith("/api/harnesses", {});
    expect(apiFetchMock).not.toHaveBeenCalled();
    expect(result.harnesses.value.map((each) => each.version)).toEqual(["2.0.18"]);
    expect(JSON.parse(window.localStorage.getItem("weave:saved:harnesses:m-mac")!).harnesses[0].version).toBe("2.0.18");
    expect(window.localStorage.getItem("weave:saved:harnesses:home")).toBeNull();
  });
});
