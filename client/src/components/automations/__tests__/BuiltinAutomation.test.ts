import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h } from "vue";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import type { Automation, AutomationRun } from "@/stores/automations";

const { apiFetchMock, navigate } = vi.hoisted(() => ({ apiFetchMock: vi.fn(), navigate: vi.fn() }));
vi.mock("@/lib/api-client", () => ({
  apiFetch: apiFetchMock,
  apiFetchOn: (_machine: unknown, path: string, init?: RequestInit) => apiFetchMock(path, init),
}));
vi.mock("@tanstack/vue-router", () => ({ useNavigate: () => navigate, useRouter: () => ({ navigate }) }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: () => () => {} }));
vi.mock("@/lib/automations", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/automations")>()),
  browserTimeZone: () => "Africa/Johannesburg",
}));

import AutomationDetailPanel from "../AutomationDetailPanel.vue";
import AutomationsNavPanel from "../AutomationsNavPanel.vue";
import { useAutomationsNav } from "@/composables/use-automations-nav";
import { useSettingsNav } from "@/composables/use-settings-nav";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const dreaming: Automation = {
  id: "a-dream", name: "Dreaming", prompt: "", triggerType: "schedule", triggerConfig: "0 3 * * *",
  maxConcurrentRuns: 1, maxRunsPerHour: 10, timeoutMinutes: 30, isEnabled: true, workspaceId: null, model: null,
  agent: null, createdAt: "2026-10-01T00:00:00Z", updatedAt: null, targetType: "dreaming", timeZone: "Africa/Johannesburg",
  nextRunAt: "2026-10-28T01:00:00Z", lastRun: null, builtin: "dreaming",
};
const mine: Automation = { ...dreaming, id: "a-mine", name: "Weekly bump", builtin: null, targetType: "new_session", prompt: "Bump" };

const run = (id: string, extra: Partial<AutomationRun>): AutomationRun => ({
  id, automationId: "a-dream", trigger: "schedule", scheduledFor: "2026-10-27T01:00:00Z", startedAt: "2026-10-27T01:00:00Z",
  state: "done", sessionId: null, instanceId: null, error: null, summary: null, ...extra,
});

let list: Automation[] = [];
let runs: AutomationRun[] = [];
const sent: { method: string; url: string; body: Record<string, unknown> }[] = [];

function serve(url: string, init?: RequestInit): Response {
  const method = init?.method ?? "GET";
  if (method !== "GET") sent.push({ method, url, body: init?.body ? JSON.parse(String(init.body)) : {} });
  if (url === "/api/automations") return json({ automations: list });
  if (/\/runs$/.test(url)) return json({ runs });
  if (method === "PUT" && url === "/api/memory/dreaming/nightly") return json({ time: "04:30", timeZone: "Africa/Johannesburg" });
  if (method === "POST" && /\/disable$/.test(url)) return json({});
  return json({ error: `not faked: ${method} ${url}` }, 404);
}

const Screen = defineComponent({
  setup() {
    const nav = useAutomationsNav();
    return () => h("div", [
      h(AutomationsNavPanel, { modelValue: nav.activeAutomationId.value, "onUpdate:modelValue": nav.setActiveAutomation, onCreate: nav.startCreate }),
      h(AutomationDetailPanel),
    ]);
  },
});

describe("The built-in Dreaming automation", () => {
  let wrapper: VueWrapper;
  const byId = (id: string) => wrapper.find(`[data-testid='${id}']`);

  async function open(): Promise<void> {
    wrapper = mount(Screen, { attachTo: document.body });
    await flushPromises();
    await wrapper.findAll("[data-testid='automation-row']")[0].trigger("click");
    await flushPromises();
  }

  beforeEach(() => {
    setActivePinia(createPinia());
    useAutomationsNav().clearSelection();
    list = [mine, dreaming];
    runs = [];
    sent.length = 0;
    apiFetchMock.mockReset();
    apiFetchMock.mockImplementation(async (url: string, init?: RequestInit) => serve(url, init));
  });
  afterEach(() => wrapper?.unmount());

  it("is first in the list with a Built in pill, and only it has one", async () => {
    await open();
    const rows = wrapper.findAll("[data-testid='automation-row']");
    expect(rows[0].text()).toContain("Dreaming");
    expect(rows[0].find("[data-testid='automation-row-builtin']").text()).toBe("Built in");
    expect(rows[1].find("[data-testid='automation-row-builtin']").exists()).toBe(false);
  });

  it("opens a compact view with its time and no delete or editing", async () => {
    await open();
    expect(byId("builtin-description").text()).toContain("Every day at 03:00.");
    expect(wrapper.text()).toContain("Experimental");
    expect(byId("automation-delete").exists()).toBe(false);
    expect(byId("automation-more-menu").exists()).toBe(false);
    expect(wrapper.find("textarea, [contenteditable]").exists()).toBe(false);
  });

  it("changes the time through the nightly endpoint in the browser's zone", async () => {
    await open();
    await byId("builtin-change-time").trigger("click");
    await byId("builtin-time").setValue("04:30");
    await byId("builtin-time-save").trigger("click");
    await flushPromises();
    expect(sent.find((s) => s.url === "/api/memory/dreaming/nightly")).toMatchObject({
      method: "PUT", body: { time: "04:30", timeZone: "Africa/Johannesburg" },
    });
    expect(sent.some((s) => s.url === "/api/automations/a-dream")).toBe(false);
  });

  it("pauses by disabling the automation", async () => {
    await open();
    await byId("builtin-pause").trigger("click");
    await flushPromises();
    expect(sent.some((s) => s.method === "POST" && s.url === "/api/automations/a-dream/disable")).toBe(true);
  });

  it("opens Settings → Memory", async () => {
    await open();
    await byId("builtin-settings").trigger("click");
    expect(useSettingsNav().activeSection.value).toBe("memory");
    expect(navigate).toHaveBeenCalledWith({ to: "/settings" });
  });

  it("shows a night's summary and a skipped night's reason", async () => {
    runs = [
      run("r1", { summary: "weave-fleet: 4 notes changed · weave-website: 1 note added · 9 min, 214k tokens" }),
      run("r2", { state: "skipped", error: "Skipped: no new sessions since the last run, nothing to read." }),
    ];
    await open();
    const rows = wrapper.findAll("[data-testid='automation-run']");
    expect(rows[0].text()).toContain("weave-fleet: 4 notes changed");
    expect(rows[0].text()).toContain("Done");
    expect(rows[1].text()).toContain("nothing to read");
    expect(rows[1].text()).toContain("Skipped");
  });
});
