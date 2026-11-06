import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";

const { apiFetch, apiFetchOn, machinesState, phone } = vi.hoisted(() => ({
  apiFetch: vi.fn(),
  apiFetchOn: vi.fn(),
  machinesState: {
    entries: [{ key: "home", name: "Desk", isLive: true, connection: null }] as Array<Record<string, unknown>>,
    others: {} as Record<string, unknown>,
  },
  phone: { value: false },
}));

vi.mock("@/lib/api-client", () => ({ apiFetch, apiFetchOn }));
vi.mock("@/stores/machines", () => ({
  useMachinesStore: () => ({
    get entries() { return machinesState.entries; },
    get others() { return machinesState.others; },
    get live() { return machinesState.entries.find((entry) => entry.isLive); },
  }),
}));
vi.mock("@/composables/use-media-query", () => ({ useMediaQuery: () => ({ get value() { return phone.value; } }) }));
vi.mock("@/lib/machine-target", () => ({
  liveTarget: () => ({ key: "home", isLive: true, connection: null }),
  targetFor: (connection: { id: string } | null) => ({ key: connection?.id ?? "home", isLive: false, connection }),
}));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: () => () => undefined, onReconnect: () => () => undefined }));
vi.mock("@/composables/use-weave-socket", () => ({ onReconnect: () => () => undefined }));

import UsageLimitsPopover from "@/components/layout/UsageLimitsPopover.vue";
import { resetUsageLimitsForTests } from "@/composables/use-usage-limits";

const inHours = (hours: number) => new Date(Date.now() + hours * 3_600_000).toISOString();
const provider = (over: Record<string, unknown>) => ({
  provider: "claude", account: null, name: "Claude", state: "ok", source: "Claude Code", harnesses: [], readAt: inHours(0), windows: [], credits: null, ...over,
});

function serve(providers: unknown[]) {
  apiFetch.mockImplementation(async (path: string) => ({
    ok: true,
    status: 200,
    json: async () => (path.startsWith("/api/usage/limits") ? { enabled: true, providers } : {}),
  }));
}

async function mountOpen(providers: unknown[]) {
  serve(providers);
  const wrapper = mount(UsageLimitsPopover, { attachTo: document.body, global: { stubs: { teleport: false } } });
  await flushPromises();
  await wrapper.get("[data-testid=usage-limits-chip]").trigger("click");
  await flushPromises();
  return wrapper;
}

describe("UsageLimitsPopover", () => {
  beforeEach(() => {
    resetUsageLimitsForTests();
    apiFetch.mockReset();
    apiFetchOn.mockReset();
    phone.value = false;
    machinesState.entries = [{ key: "home", name: "Desk", isLive: true, connection: null }];
    machinesState.others = {};
  });
  afterEach(() => {
    document.body.innerHTML = "";
  });

  it("shows the tightest window on the chip: grey below 80%, amber above, red when used up", async () => {
    serve([provider({ windows: [{ window: "five_hour", utilization: 0.42, resetsAt: inHours(2), status: "allowed" }] })]);
    const wrapper = mount(UsageLimitsPopover);
    await flushPromises();
    const chip = wrapper.get("[data-testid=usage-limits-chip]");
    expect(chip.text()).toContain("Claude 5 h");
    expect(chip.text()).toContain("42%");
    expect(chip.classes()).toContain("usage-chip--quiet");
    wrapper.unmount();

    resetUsageLimitsForTests();
    serve([provider({ windows: [{ window: "five_hour", utilization: 0.85, resetsAt: inHours(2), status: "warning" }] })]);
    const warn = mount(UsageLimitsPopover);
    await flushPromises();
    expect(warn.get("[data-testid=usage-limits-chip]").classes()).toContain("usage-chip--warn");
    warn.unmount();

    resetUsageLimitsForTests();
    serve([provider({ windows: [{ window: "five_hour", utilization: 0.5, resetsAt: inHours(2), status: "rejected" }] })]);
    const used = mount(UsageLimitsPopover);
    await flushPromises();
    expect(used.get("[data-testid=usage-limits-chip]").classes()).toContain("usage-chip--used");
    expect(used.text()).toContain("100%");
    used.unmount();
  });

  it("shows nothing without a provider", async () => {
    serve([]);
    const wrapper = mount(UsageLimitsPopover);
    await flushPromises();
    expect(wrapper.find("[data-testid=usage-limits-chip]").exists()).toBe(false);
    wrapper.unmount();
  });

  it("lists a bar per window with percent, reset, source and the OpenRouter credits", async () => {
    const wrapper = await mountOpen([
      provider({
        account: "Max plan",
        windows: [
          { window: "five_hour", utilization: 0.42, resetsAt: inHours(2), status: "allowed" },
          { window: "seven_day", utilization: 0.1, resetsAt: inHours(100), status: "allowed" },
        ],
      }),
      provider({ provider: "openrouter", name: "OpenRouter", source: "OpenCode", credits: { remaining: 7.6, limit: 10, used: 2.4, unit: null } }),
    ]);
    const claude = document.body.querySelector("[data-testid=usage-provider-claude]")!;
    expect(claude.textContent).toContain("Max plan");
    expect(claude.textContent).toContain("read from Claude Code, last asked");
    expect(claude.querySelectorAll("[role=progressbar]")).toHaveLength(2);
    expect(claude.querySelector("[role=progressbar]")!.getAttribute("aria-valuenow")).toBe("42");
    expect(claude.textContent).toContain("5-hour limit");
    expect(claude.textContent).toContain("resets");
    expect(document.body.querySelector("[data-testid=usage-provider-openrouter]")!.textContent).toContain("$7.60 left of $10.00");
    wrapper.unmount();
  });

  it("shows GitHub's AI credits as a count, never dollars, even with no window", async () => {
    const wrapper = await mountOpen([
      provider({ provider: "copilot", name: "GitHub Copilot", state: "no_window", credits: { remaining: null, limit: null, used: 307846, unit: "ai_credits" } }),
    ]);
    const text = document.body.querySelector("[data-testid=usage-provider-copilot]")!.textContent!;
    expect(text).toContain("Pay-per-token: no limits to show.");
    expect(text).toContain("AI credits used this month: 307,846");
    expect(text).not.toContain("$");
    expect(text).not.toContain("Credits:");
    expect(text).not.toContain("Can't read this right now");
    wrapper.unmount();
  });

  it("labels the organization's pooled limit", async () => {
    const wrapper = await mountOpen([
      provider({ provider: "copilot", name: "GitHub Copilot", windows: [{ window: "organization", utilization: 1, resetsAt: inHours(100), status: "rejected" }] }),
    ]);
    const text = document.body.querySelector("[data-testid=usage-provider-copilot]")!.textContent!;
    expect(text).toContain("Organization limit");
    expect(text).toContain("100%");
    expect(document.body.querySelector("[data-testid=usage-limits-chip]")!.textContent).toContain("GitHub Copilot org");
    expect(text).not.toContain("Can't read this right now");
    wrapper.unmount();
  });

  it("explains each state that has no bars", async () => {
    const wrapper = await mountOpen([
      provider({ provider: "a", name: "Alpha", state: "no_window" }),
      provider({ provider: "b", name: "Beta", state: "unreadable" }),
      provider({ provider: "c", name: "Gamma", state: "signed_out" }),
      provider({ provider: "d", name: "Delta", state: "unsupported", source: null }),
    ]);
    const text = (id: string) => document.body.querySelector(`[data-testid=usage-provider-${id}]`)!.textContent;
    expect(text("a")).toContain("Pay-per-token: no limits to show");
    expect(text("b")).toContain("Can't read this right now");
    expect(text("c")).toContain("Sign in again in Claude Code");
    expect(text("d")).toContain("Fleet can't read this sign-in yet");
    // Each state says only its own line.
    expect(text("a")).not.toContain("Can't read this right now");
    expect(text("c")).not.toContain("Can't read this right now");
    wrapper.unmount();
  });

  it("asks before reading a provider, and Allow sends the consent", async () => {
    const wrapper = await mountOpen([provider({ provider: "codex", name: "Codex", state: "not_allowed", source: "Codex CLI" })]);
    const section = document.body.querySelector("[data-testid=usage-provider-codex]")!;
    expect(section.textContent).toContain("read-only, never saves it");
    expect(section.querySelectorAll("[role=progressbar]")).toHaveLength(0);

    (document.body.querySelector("[data-testid=usage-allow-codex]") as HTMLButtonElement).click();
    await flushPromises();
    expect(apiFetch).toHaveBeenCalledWith("/api/usage/limits/codex/allow", { method: "POST" });
    wrapper.unmount();
  });

  it("asks the server to refresh (watch) while open", async () => {
    const wrapper = await mountOpen([provider({ windows: [{ window: "five_hour", utilization: 0.4, resetsAt: inHours(1), status: "allowed" }] })]);
    expect(apiFetch).toHaveBeenCalledWith("/api/usage/limits?watch=true");
    wrapper.unmount();
  });

  it("draws a tick at an even share and the pace note only when ahead of pace", async () => {
    // 3 of 5 hours gone (60%), 90% used: ahead of pace. The 7-day has no length: no tick.
    const wrapper = await mountOpen([
      provider({
        windows: [
          { window: "five_hour", utilization: 0.9, resetsAt: inHours(2), windowSeconds: 18000, status: "warning" },
          { window: "seven_day", utilization: 0.1, resetsAt: inHours(80), status: "allowed" },
        ],
      }),
    ]);
    const ticks = [...document.body.querySelectorAll("[data-testid=usage-pace-tick]")];
    expect(ticks).toHaveLength(1);
    expect(ticks[0].getAttribute("title")).toMatch(/^An even share of this window by now: 6\d%$/);
    const notes = document.body.querySelectorAll("[data-testid=usage-pace-note]");
    expect(notes).toHaveLength(1);
    expect(notes[0].textContent).toContain("Ahead of pace");
    wrapper.unmount();
  });

  it("shows a tick but no note when under pace", async () => {
    const wrapper = await mountOpen([
      provider({ windows: [{ window: "five_hour", utilization: 0.2, resetsAt: inHours(2), windowSeconds: 18000, status: "allowed" }] }),
    ]);
    expect(document.body.querySelectorAll("[data-testid=usage-pace-tick]")).toHaveLength(1);
    expect(document.body.querySelector("[data-testid=usage-pace-note]")).toBeNull();
    wrapper.unmount();
  });

  it("shows no machine headings with one machine", async () => {
    const wrapper = await mountOpen([provider({ windows: [{ window: "five_hour", utilization: 0.4, resetsAt: inHours(1), status: "allowed" }] })]);
    expect(document.body.querySelector("[data-testid=usage-machine]")).toBeNull();
    wrapper.unmount();
  });

  describe("two machines", () => {
    const twoMachines = () => {
      machinesState.entries = [
        { key: "home", name: "Desk", isLive: true, connection: null },
        { key: "lap", name: "Laptop", isLive: false, connection: { id: "lap" } },
        { key: "off", name: "Away", isLive: false, connection: { id: "off" } },
      ];
      machinesState.others = { lap: { loadedAt: 1, error: null }, off: { loadedAt: 1, error: "down" } };
    };
    const codex = (over: Record<string, unknown> = {}) =>
      provider({ provider: "codex", name: "Codex", accountKey: "k1", windows: [{ window: "primary", utilization: 0.3, resetsAt: inHours(1), status: "allowed" }], ...over });

    it("groups by connected machine, shows a shared account once with also-on, and asks the other lazily", async () => {
      twoMachines();
      apiFetchOn.mockResolvedValue({ ok: true, json: async () => ({ enabled: true, providers: [codex(), provider({ provider: "openrouter", name: "OpenRouter" })] }) });
      const wrapper = await mountOpen([codex(), provider({})]);
      const headings = [...document.body.querySelectorAll("[data-testid=usage-machine]")].map((h) => h.textContent?.trim());
      expect(headings).toEqual(["Desk", "Laptop"]);
      expect(apiFetchOn).toHaveBeenCalledTimes(1);
      expect(apiFetchOn).toHaveBeenCalledWith({ id: "lap" }, "/api/usage/limits?watch=true");
      expect(document.body.querySelectorAll("[data-testid=usage-provider-codex]")).toHaveLength(1);
      expect(document.body.querySelector("[data-testid=usage-also-on]")!.textContent).toContain("also on Laptop");
      expect(document.body.querySelector("[data-testid=usage-provider-openrouter]")).not.toBeNull();
      wrapper.unmount();
    });

    it("sends Allow only for the live machine's providers", async () => {
      twoMachines();
      apiFetchOn.mockResolvedValue({ ok: true, json: async () => ({ enabled: true, providers: [provider({ provider: "openrouter", name: "OpenRouter", state: "not_allowed" })] }) });
      const wrapper = await mountOpen([provider({})]);
      expect(document.body.querySelector("[data-testid=usage-allow-openrouter]")).toBeNull();
      expect(document.body.textContent).toContain("Open Laptop to allow it there.");
      wrapper.unmount();
    });
  });

  it("is a bottom sheet on a phone, with the same list", async () => {
    phone.value = true;
    const wrapper = await mountOpen([provider({ windows: [{ window: "five_hour", utilization: 0.4, resetsAt: inHours(1), status: "allowed" }] })]);
    expect(document.body.querySelector("[data-testid=usage-limits-popover]")).toBeNull();
    const sheet = document.body.querySelector("[data-testid=usage-limits-sheet]")!;
    expect(sheet.querySelector("[data-testid=usage-provider-claude]")).not.toBeNull();
    expect(sheet.querySelectorAll("[role=progressbar]")).toHaveLength(1);
    wrapper.unmount();
  });
});
