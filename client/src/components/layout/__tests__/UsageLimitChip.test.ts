import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import type { DomainEvent } from "@/lib/domain-events";

const { apiGet, globalHandlers } = vi.hoisted(() => ({
  apiGet: vi.fn(),
  globalHandlers: [] as Array<(event: DomainEvent) => void>,
}));

vi.mock("@/api/client", () => ({ api: { GET: apiGet, POST: vi.fn() } }));
vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: (_topic: string, handler: (event: DomainEvent) => void) => {
    globalHandlers.push(handler);
    return () => undefined;
  },
  onReconnect: () => () => undefined,
}));
vi.mock("@/composables/use-weave-socket", () => ({ onReconnect: () => () => undefined }));
vi.mock("@/composables/use-harnesses", () => ({
  useHarnesses: () => ({ harnesses: { value: [{ type: "claude-code", displayName: "Claude Code" }] } }),
}));

import UsageLimitChip from "@/components/layout/UsageLimitChip.vue";
import { resetHarnessUsageForTests } from "@/composables/use-harness-usage";

const inHours = (hours: number) => new Date(Date.now() + hours * 3_600_000).toISOString();

function push(windows: Array<Record<string, unknown>>): void {
  for (const handler of globalHandlers) {
    handler({ type: "harness.usage", payload: { harnessType: "claude-code", windows, updatedAt: inHours(0) } } as unknown as DomainEvent);
  }
}

describe("UsageLimitChip", () => {
  beforeEach(() => {
    resetHarnessUsageForTests();
    globalHandlers.length = 0;
    apiGet.mockReset();
    apiGet.mockResolvedValue({ data: [], response: { ok: true } });
  });

  afterEach(() => vi.useRealTimers());

  it("shows nothing while every window has room, or when the harness reports no limits", async () => {
    const wrapper = mount(UsageLimitChip);
    await flushPromises();
    expect(wrapper.find('[data-testid="usage-limit-chip"]').exists()).toBe(false);

    push([{ window: "five_hour", utilization: 0.79, resetsAt: inHours(2), status: "allowed" }]);
    await flushPromises();
    expect(wrapper.find('[data-testid="usage-limit-chip"]').exists()).toBe(false);
    wrapper.unmount();
  });

  it("speaks up from 80%: which harness and window, how full, and when it resets", async () => {
    const wrapper = mount(UsageLimitChip);
    await flushPromises();

    push([
      { window: "five_hour", utilization: 0.82, resetsAt: inHours(2), status: "warning" },
      { window: "seven_day", utilization: 0.63, resetsAt: inHours(80), status: "allowed" },
    ]);
    await flushPromises();

    const chip = wrapper.get('[data-testid="usage-limit-chip"]');
    const time = new Date(inHours(2)).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
    expect(chip.text().replace(/\s+/g, " ")).toBe(`Claude 5 h resets ${time}`);
    expect(chip.attributes("title")).toBe(`Claude's 5-hour limit is 82% used; it resets ${time}.`);
    expect(chip.classes()).not.toContain("usage-chip--used");
    wrapper.unmount();
  });

  it("says when a window is used up, and shows the limits loaded with the page", async () => {
    apiGet.mockResolvedValue({
      data: [{ harnessType: "claude-code", windows: [{ window: "seven_day", utilization: 1, resetsAt: inHours(30), status: "rejected" }], updatedAt: inHours(0) }],
      response: { ok: true },
    });
    const wrapper = mount(UsageLimitChip);
    await flushPromises();

    const chip = wrapper.get('[data-testid="usage-limit-chip"]');
    expect(apiGet).toHaveBeenCalledWith("/api/harnesses/usage");
    expect(chip.classes()).toContain("usage-chip--used");
    expect(chip.text()).toContain("Claude week");
    expect(chip.attributes("title")).toMatch(/^Claude's weekly limit is used up; it resets /);
    wrapper.unmount();
  });
});
