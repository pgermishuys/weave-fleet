import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import FeaturesSection from "@/components/settings/FeaturesSection.vue";
import type { ModsRuntimeJob, ModsRuntimeView } from "@/lib/mods-runtime";

const { getMock, putMock } = vi.hoisted(() => ({ getMock: vi.fn(), putMock: vi.fn() }));

vi.mock("@/api/client", () => ({ api: { GET: getMock, PUT: putMock } }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: () => () => {} }));

const NOW = Date.parse("2026-10-10T12:00:00Z");
const RUNTIME = "/api/features/mods/runtime";

function makeView(overrides: Partial<ModsRuntimeView> = {}): ModsRuntimeView {
  const release = { version: "1.4.2", size: 35_300_000, hasBuild: true, installFolder: "~/.weave/runtimes/bun/1.4.2", source: "github.com/oven-sh/bun" };
  return { bun: null, release, job: null, ...overrides };
}

function installed(version = "1.4.2"): ModsRuntimeView["bun"] {
  return { path: `/u/bun/${version}/bun`, displayPath: `~/.weave/runtimes/bun/${version}/bun`, source: "installed", version, safe: true };
}

function job(overrides: Partial<ModsRuntimeJob> = {}): ModsRuntimeJob {
  const startedAt = new Date(NOW - 40_000).toISOString();
  return { phase: "downloading", version: "1.4.2", bytesReceived: 12_000_000, bytesTotal: 35_300_000, startedAt, ...overrides };
}

let server: { view: ModsRuntimeView; calls: string[]; overrides: Record<string, () => Response> };

const json = (body: unknown) => new Response(JSON.stringify(body), { headers: { "content-type": "application/json" } });

async function mountRow(view: ModsRuntimeView, preference?: "true" | "false"): Promise<VueWrapper> {
  server.view = view;
  getMock.mockResolvedValue({ data: preference ? { Mods: preference } : {} });
  const wrapper = mount(FeaturesSection, { attachTo: document.body });
  await flushPromises();
  return wrapper;
}

const panel = (wrapper: VueWrapper) => wrapper.find("[data-testid='mods-panel']");
const stateOf = (wrapper: VueWrapper) => panel(wrapper).attributes("data-state");
const buttons = (wrapper: Pick<VueWrapper, "findAll">) => wrapper.findAll("button").map((item) => item.text());
const button = (wrapper: VueWrapper, label: string) => wrapper.findAll("button").find((item) => item.text() === label)!;
const toggle = (wrapper: VueWrapper) => wrapper.get("[data-testid='mods-switch']");
const called = (action: string) => server.calls.filter((call) => call === `POST ${RUNTIME}/${action}`);
const step = (wrapper: VueWrapper, id: string) => wrapper.get(`[data-testid='mods-step-${id}']`).attributes("data-status");

describe("Mods row: turning Mods on", () => {
  beforeEach(() => {
    localStorage.clear();
    setActivePinia(createPinia());
    getMock.mockReset();
    putMock.mockReset().mockResolvedValue({});
    server = { view: makeView(), calls: [], overrides: {} };
    vi.stubGlobal(
      "fetch",
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const path = new URL(String(input), "http://localhost").pathname;
        const key = `${(init?.method ?? "GET").toUpperCase()} ${path}`;
        server.calls.push(key);
        if (server.overrides[key]) return server.overrides[key]!();
        return path === "/api/features/mods" ? json({ on: false }) : json(server.view);
      }),
    );
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(NOW);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    document.body.innerHTML = "";
  });

  it("1 off: shows nothing under the description", async () => {
    const wrapper = await mountRow(makeView());

    expect(toggle(wrapper).attributes("aria-checked")).toBe("false");
    expect(panel(wrapper).exists()).toBe(false);
  });

  it("2 confirm: switching on with no Bun says what Fleet will download, and the switch stays off", async () => {
    const wrapper = await mountRow(makeView());

    await toggle(wrapper).trigger("click");

    expect(stateOf(wrapper)).toBe("confirm");
    expect(panel(wrapper).text()).toContain("Fleet downloads Bun 1.4.2 (about 35 MB) from github.com/oven-sh/bun");
    expect(panel(wrapper).text()).toContain("installs it in ~/.weave/runtimes/bun/1.4.2. Nothing else on your computer changes.");
    expect(toggle(wrapper).attributes("aria-checked")).toBe("false");
    expect(toggle(wrapper).classes()).toContain("ring-2");
    expect(putMock).not.toHaveBeenCalled();
    expect(buttons(panel(wrapper))).toEqual(["Turn on and install", "Cancel"]);
  });

  it("2 confirm: Cancel folds the row back to step 1", async () => {
    const wrapper = await mountRow(makeView());
    await toggle(wrapper).trigger("click");

    await button(wrapper, "Cancel").trigger("click");

    expect(panel(wrapper).exists()).toBe(false);
    expect(called("install")).toHaveLength(0);
  });

  it("2 confirm: with no build for this computer it says so and only offers Cancel", async () => {
    const wrapper = await mountRow(makeView({ release: { ...makeView().release, hasBuild: false } }));

    await toggle(wrapper).trigger("click");

    expect(panel(wrapper).text()).toContain("Fleet has no Bun build for this computer. Install Bun yourself and point Fleet at it.");
    expect(buttons(panel(wrapper))).toEqual(["Cancel"]);
  });

  it("2 confirm: Turn on and install posts the install and shows its progress", async () => {
    const wrapper = await mountRow(makeView());
    await toggle(wrapper).trigger("click");
    server.overrides[`POST ${RUNTIME}/install`] = () => json(makeView({ job: job() }));

    await button(wrapper, "Turn on and install").trigger("click");
    await flushPromises();

    expect(called("install")).toHaveLength(1);
    expect(stateOf(wrapper)).toBe("progress");
    expect(panel(wrapper).text()).toContain("This keeps going if you leave Settings. Mods start as soon as it's done.");
    expect(toggle(wrapper).classes()).not.toContain("ring-2");
  });

  it("3a downloading: heading, megabytes, bar, steps and the announcement", async () => {
    const wrapper = await mountRow(makeView({ job: job() }), "true");

    expect(stateOf(wrapper)).toBe("progress");
    expect(panel(wrapper).text()).toContain("Downloading Bun 1.4.2");
    expect(panel(wrapper).text()).toContain("12 of 35 MB");
    const bar = wrapper.get("[role='progressbar']");
    expect([bar.attributes("aria-valuenow"), bar.attributes("aria-valuemax")]).toEqual(["12", "35"]);
    expect([step(wrapper, "download"), step(wrapper, "check")]).toEqual(["current", "todo"]);
    expect(wrapper.get("[aria-live='polite']").text()).toBe("Downloading Bun 1.4.2");
  });

  it.each([
    ["3b", "verifying", "Checking the download", ["done", "current", "todo"]],
    ["3c", "extracting", "Unpacking", ["done", "done", "current"]],
  ] as const)("%s %s: the heading and steps move on", async (_id, phase, heading, steps) => {
    const wrapper = await mountRow(makeView({ job: job({ phase }) }), "true");

    expect(panel(wrapper).text()).toContain(heading);
    expect([step(wrapper, "download"), step(wrapper, "check"), step(wrapper, "unpack")]).toEqual(steps);
  });

  it("3d came back: an install already running when the row loaded says how long it has been going", async () => {
    const wrapper = await mountRow(makeView({ job: job() }), "true");

    expect(panel(wrapper).text()).toContain("Started 40 seconds ago. It kept going while you were away.");
  });

  it("3 Cancel posts cancel", async () => {
    const wrapper = await mountRow(makeView({ job: job() }), "true");
    server.overrides[`POST ${RUNTIME}/cancel`] = () => json(makeView({ job: job({ phase: "failed", reason: "cancelled" }) }));

    await button(wrapper, "Cancel").trigger("click");
    await flushPromises();

    expect(called("cancel")).toHaveLength(1);
    expect(panel(wrapper).exists()).toBe(false);
  });

  it("4 ready: Mods on with the version and the path", async () => {
    const wrapper = await mountRow(makeView({ bun: installed() }), "true");

    expect(stateOf(wrapper)).toBe("ready");
    expect(panel(wrapper).text()).toContain("Mods on · Bun 1.4.2");
    expect(panel(wrapper).text()).toContain("~/.weave/runtimes/bun/1.4.2/bun");
  });

  it("4 ready: with a Bun from config it says your Bun", async () => {
    const bun = { ...installed("1.4.5")!, source: "configured" as const, displayPath: "/opt/bun/bin/bun" };
    const wrapper = await mountRow(makeView({ bun, configuredPath: bun.path }), "true");

    expect(panel(wrapper).text()).toContain("Mods on · your Bun 1.4.5");
    expect(panel(wrapper).text()).toContain("/opt/bun/bin/bun");
  });

  it.each([
    ["5a", "offline", "Fleet couldn't reach github.com: the connection timed out.", "If GitHub is blocked here, install Bun yourself and point Fleet at it."],
    ["5b", "blocked", "A proxy answered 403.", "Ask whoever runs your network to allow github.com/oven-sh/bun"],
  ])("%s failed %s: red heading, the message, the hint and Retry; the switch is off", async (_id, reason, message, hint) => {
    const wrapper = await mountRow(makeView({ job: job({ phase: "failed", reason, message }) }), "false");

    expect(stateOf(wrapper)).toBe("failed");
    expect(panel(wrapper).get("h4").text()).toBe("⚠ Couldn't download Bun");
    expect(panel(wrapper).get("h4").classes()).toContain("text-error");
    expect(panel(wrapper).text()).toContain(message);
    expect(panel(wrapper).text()).toContain(hint);
    expect(buttons(wrapper)).toContain("Retry");
    expect(toggle(wrapper).attributes("aria-checked")).toBe("false");
  });

  it("5 failed with no build: shows the message and hides Retry", async () => {
    const failed = job({ phase: "failed", reason: "no-build", message: "Fleet has no Bun build for this computer." });
    const wrapper = await mountRow(makeView({ job: failed }), "false");

    expect(stateOf(wrapper)).toBe("failed");
    expect(buttons(wrapper)).not.toContain("Retry");
  });

  it("5 failed: Retry posts the install again", async () => {
    const wrapper = await mountRow(makeView({ job: job({ phase: "failed", reason: "other", message: "The download stopped." }) }), "false");
    server.overrides[`POST ${RUNTIME}/install`] = () => json(makeView({ job: job() }));

    await button(wrapper, "Retry").trigger("click");
    await flushPromises();

    expect(called("install")).toHaveLength(1);
    expect(stateOf(wrapper)).toBe("progress");
  });

  it("6 on at once: with a Bun already there, switching on just writes the preference", async () => {
    const wrapper = await mountRow(makeView({ bun: installed() }), "false");

    await toggle(wrapper).trigger("click");
    await flushPromises();

    expect(putMock).toHaveBeenCalledTimes(1);
    expect(called("install")).toHaveLength(0);
    expect(stateOf(wrapper)).not.toBe("confirm");
  });
});
