import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";

const { apiFetch } = vi.hoisted(() => ({ apiFetch: vi.fn() }));

vi.mock("@/lib/api-client", () => ({ apiFetch }));
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
      provider({ provider: "openrouter", name: "OpenRouter", source: "OpenCode", credits: { remaining: 7.6, limit: 10, used: 2.4 } }),
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
});
