import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("@/api/client", () => ({
  api: { GET: vi.fn(), POST: vi.fn(), PUT: vi.fn(), DELETE: vi.fn(), PATCH: vi.fn() },
}));

import { api, type SessionListItem } from "@/api/client";
import ContextRing from "@/components/session/ContextRing.vue";
import { _resetSessionContextForTesting, publishSessionContext } from "@/composables/use-session-context";
import { resetHarnessUsageForTests } from "@/composables/use-harness-usage";
import { toContextUsage } from "@/lib/context-usage";
import { useSessionsStore } from "@/stores/sessions";

const mockApi = vi.mocked(api);

function publish(overrides: Record<string, unknown> = {}): void {
  publishSessionContext("s1", toContextUsage({
    sessionId: "s1",
    used: 76_000,
    limit: 200_000,
    compactsAt: 167_000,
    modelId: "claude-opus-5",
    lastCall: { input: 1_000, cacheRead: 74_500, cacheWrite: 0, output: 500, reasoning: 0, used: 76_000 },
    compacting: false,
    turns: [
      { used: 40_000, limit: 200_000, at: "2026-10-06T10:00:00Z", afterCompaction: false },
      { used: 76_000, limit: 200_000, at: "2026-10-06T10:05:00Z", afterCompaction: false },
    ],
    updatedAt: "2026-10-06T10:05:00Z",
    ...overrides,
  }));
}

function seedSession(capabilities: Record<string, unknown> = {}, totalCost = 1.42, harnessType = "claude-code"): void {
  useSessionsStore().setSessions([{
    harnessType,
    session: { id: "s1", title: "T", time: { created: 1, updated: 2 }, tags: [] },
    totalCost,
    capabilities: { canCompact: true, compactDisabledReason: null, ...capabilities },
  } as unknown as SessionListItem]);
}

function mountRing() {
  return mount(ContextRing, { props: { sessionId: "s1" }, attachTo: document.body, global: { stubs: { teleport: false } } });
}

function popover(): HTMLElement | null {
  return document.querySelector<HTMLElement>('[data-testid="context-popover"]');
}

describe("ContextRing", () => {
  beforeEach(() => {
    _resetSessionContextForTesting();
    resetHarnessUsageForTests();
    mockApi.POST.mockReset();
    mockApi.GET.mockReset();
    mockApi.GET.mockResolvedValue({ data: [], response: { ok: true } } as never);
    seedSession();
  });

  afterEach(() => {
    document.body.innerHTML = "";
  });

  it("shows nothing until the session's harness has reported a call", () => {
    const wrapper = mountRing();
    expect(wrapper.find('[data-testid="context-ring"]').exists()).toBe(false);
    wrapper.unmount();
  });

  it("is a quiet ring under three-quarters, and says how full on hover", async () => {
    publish();
    const wrapper = mountRing();
    await flushPromises();

    const ring = wrapper.get('[data-testid="context-ring"]');
    expect(ring.attributes("data-tone")).toBe("ok");
    expect(ring.text()).toBe("");
    expect(ring.attributes("title")).toBe("38% of the context window used · details");
    wrapper.unmount();
  });

  it("shows the percentage once it's getting full, in the tone that says so", async () => {
    publish({ used: 182_000 });
    const wrapper = mountRing();
    await flushPromises();

    const ring = wrapper.get('[data-testid="context-ring"]');
    expect(ring.attributes("data-tone")).toBe("danger");
    expect(ring.text()).toBe("91%");
    wrapper.unmount();
  });

  it("opens the details: the size, each turn, the last call and what the session cost", async () => {
    publish();
    const wrapper = mountRing();
    await flushPromises();

    await wrapper.get('[data-testid="context-ring"]').trigger("click");
    await flushPromises();

    const text = popover()?.textContent ?? "";
    expect(text).toContain("38%");
    expect(text).toContain("76,000 of 200,000 tokens · claude-opus-5");
    expect(text).toContain("compacts at ~84%");
    expect(text).toContain("2 turns");
    expect(text).toContain("Read from cache74,500");
    expect(text).toContain("About 2 turns");
    expect(text).toContain("$1.42");
    expect(popover()?.querySelectorAll(".context-card__bar")).toHaveLength(2);
    wrapper.unmount();
  });

  it("compacts now, and says why when Fleet refuses", async () => {
    publish();
    mockApi.POST.mockResolvedValueOnce({ data: undefined, error: undefined, response: new Response(null, { status: 202 }) } as never);
    mockApi.POST.mockResolvedValueOnce({ data: undefined, error: { error: "Wait for the agent to finish its turn, then compact." }, response: new Response(null, { status: 409 }) } as never);
    const wrapper = mountRing();
    await flushPromises();
    await wrapper.get('[data-testid="context-ring"]').trigger("click");
    await flushPromises();

    document.querySelector<HTMLButtonElement>('[data-testid="context-compact"]')!.click();
    await flushPromises();
    expect(mockApi.POST).toHaveBeenCalledWith("/api/sessions/{id}/compact", { params: { path: { id: "s1" } } });
    expect(document.querySelector('[data-testid="context-error"]')).toBeNull();

    document.querySelector<HTMLButtonElement>('[data-testid="context-compact"]')!.click();
    await flushPromises();
    expect(document.querySelector('[data-testid="context-error"]')?.textContent).toContain("Wait for the agent to finish its turn");
    wrapper.unmount();
  });

  it("can't compact when the session says it can't, with its reason", async () => {
    seedSession({ canCompact: false, compactDisabledReason: "Wait for the agent to finish its turn." });
    publish();
    const wrapper = mountRing();
    await flushPromises();
    await wrapper.get('[data-testid="context-ring"]').trigger("click");
    await flushPromises();

    const button = document.querySelector<HTMLButtonElement>('[data-testid="context-compact"]')!;
    expect(button.disabled).toBe(true);
    expect(button.title).toBe("Wait for the agent to finish its turn.");
    wrapper.unmount();
  });

  it("after a compaction, says so until the next call measures it", async () => {
    publish({ used: null, lastCall: null, compactedAt: "2026-10-06T10:06:00Z" });
    const wrapper = mountRing();
    await flushPromises();

    const ring = wrapper.get('[data-testid="context-ring"]');
    expect(ring.attributes("title")).toContain("Context compacted");
    await ring.trigger("click");
    await flushPromises();
    expect(popover()?.textContent).toContain("Compacted");
    expect(popover()?.textContent).toContain("200,000 token window");
    wrapper.unmount();
  });

  it("shows the harness's usage limits under the context, when it reports them", async () => {
    const inTwoHours = new Date(Date.now() + 2 * 3_600_000 + 5 * 60_000);
    mockApi.GET.mockResolvedValue({
      data: [{
        harnessType: "claude-code",
        windows: [
          { window: "seven_day", utilization: 0.63, resetsAt: new Date(Date.now() + 4 * 86_400_000).toISOString(), status: "allowed" },
          { window: "five_hour", utilization: 0.82, resetsAt: inTwoHours.toISOString(), status: "warning" },
        ],
        updatedAt: new Date().toISOString(),
      }],
      response: { ok: true },
    } as never);
    publish();
    const wrapper = mountRing();
    await flushPromises();

    await wrapper.get('[data-testid="context-ring"]').trigger("click");
    await flushPromises();

    expect(mockApi.GET).toHaveBeenCalledWith("/api/harnesses/usage");
    const limits = Array.from(popover()?.querySelectorAll<HTMLElement>('[data-testid="context-limit"]') ?? []);
    expect(limits.map((limit) => limit.querySelector("span")?.textContent)).toEqual(["5-hour limit", "Weekly limit"]);
    const time = inTwoHours.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
    expect(limits[0].textContent).toContain(`82% · resets ${time}`);
    expect(limits[0].dataset.tone).toBe("warn");
    expect(limits[1].textContent).toMatch(/63% · resets \w+/);
    expect(limits[1].dataset.tone).toBe("ok");
    wrapper.unmount();
  });

  it("shows no limits for a harness that reports none, as on a gateway", async () => {
    publish();
    const wrapper = mountRing();
    await flushPromises();

    await wrapper.get('[data-testid="context-ring"]').trigger("click");
    await flushPromises();

    expect(popover()?.querySelector('[data-testid="context-limits"]')).toBeNull();
    wrapper.unmount();
  });
});
