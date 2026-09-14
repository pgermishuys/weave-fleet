import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h } from "vue";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import type { Automation } from "@/stores/automations";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));

import AutomationDetailPanel from "../AutomationDetailPanel.vue";
import AutomationsNavPanel from "../AutomationsNavPanel.vue";
import { useAutomationsNav } from "@/composables/use-automations-nav";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const weekly: Automation = {
  id: "a1",
  name: "Weekly PR digest",
  prompt: "Summarise the open PRs",
  triggerType: "schedule",
  triggerConfig: "0 9 * * 1",
  maxConcurrentRuns: 1,
  maxRunsPerHour: 10,
  timeoutMinutes: 30,
  isEnabled: true,
  workspaceId: null,
  model: null,
  agent: null,
  createdAt: "2026-09-14T09:00:00Z",
  updatedAt: null,
  timeZone: null,
};

let list: Automation[] = [];
let refuseNext: { url: RegExp; error: string } | null = null;
const created: Record<string, unknown>[] = [];

function serve(url: string, init?: RequestInit): Response {
  const method = init?.method ?? "GET";
  if (refuseNext && refuseNext.url.test(`${method} ${url}`)) {
    const { error } = refuseNext;
    refuseNext = null;
    return json({ error }, 400);
  }
  if (method === "GET" && url === "/api/automations") return json({ automations: list });
  if (method === "GET" && url === "/api/automations/event-catalog") return json(["session_created", "delegation.created"]);
  if (method === "POST" && url === "/api/automations") {
    const body = JSON.parse(String(init?.body)) as Record<string, unknown>;
    created.push(body);
    const automation = { ...weekly, ...body, id: `a${list.length + 1}`, isEnabled: true } as Automation;
    list = [...list, automation];
    return json(automation, 201);
  }
  const toggle = url.match(/^\/api\/automations\/([^/]+)\/(enable|disable)$/);
  if (method === "POST" && toggle) {
    list = list.map((a) => (a.id === toggle[1] ? { ...a, isEnabled: toggle[2] === "enable" } : a));
    return new Response(null, { status: 204 });
  }
  if (method === "POST" && /\/run$/.test(url)) return new Response(null, { status: 202 });
  return json({ error: `not faked: ${method} ${url}` }, 404);
}

/** The sidebar list and the automation page, as the Automations screen shows them side by side. */
const Screen = defineComponent({
  setup() {
    const nav = useAutomationsNav();
    return () =>
      h("div", [
        h(AutomationsNavPanel, {
          modelValue: nav.activeAutomationId.value,
          "onUpdate:modelValue": nav.setActiveAutomation,
          onCreate: nav.startCreate,
        }),
        h(AutomationDetailPanel),
      ]);
  },
});

function sidebarRows(wrapper: VueWrapper) {
  return wrapper.findAll(".automations-nav__item").map((row) => ({
    name: row.find(".automation-name").text(),
    state: row.find(".status-dot").attributes("aria-label"),
  }));
}

describe("Automations screen", () => {
  let wrapper: VueWrapper;

  beforeEach(async () => {
    setActivePinia(createPinia());
    list = [weekly];
    refuseNext = null;
    created.length = 0;
    apiFetchMock.mockReset();
    apiFetchMock.mockImplementation(async (url: string, init?: RequestInit) => serve(url, init));
    useAutomationsNav().clearSelection();
    wrapper = mount(Screen, { attachTo: document.body });
    await flushPromises();
  });

  afterEach(() => {
    wrapper.unmount();
  });

  async function createSchedule(name: string, cron: string) {
    await wrapper.find(".new-automation-btn").trigger("click");
    await flushPromises();
    await wrapper.find("#automation-name").setValue(name);
    await wrapper.find("#automation-prompt").setValue("Summarise what changed last week");
    await wrapper.find("#automation-trigger-config").setValue(cron);
    await wrapper.find("form").trigger("submit");
    await flushPromises();
  }

  it("lists a new automation in the sidebar as soon as it's created", async () => {
    await createSchedule("Monday standup notes", "0 9 * * 1");

    expect(sidebarRows(wrapper)).toEqual([
      { name: "Weekly PR digest", state: "Enabled" },
      { name: "Monday standup notes", state: "Enabled" },
    ]);
    expect(wrapper.find("h2").text()).toBe("Monday standup notes");
  });

  it("saves the schedule in the browser's time zone", async () => {
    await createSchedule("Monday standup notes", "0 9 * * 1");

    expect(created[0]?.timeZone).toBe(Intl.DateTimeFormat().resolvedOptions().timeZone);
  });

  it("shows a switch-off in the sidebar too", async () => {
    await wrapper.findAll(".automations-nav__item")[0]!.trigger("click");
    await flushPromises();

    await wrapper.find("button[role=switch]").trigger("click");
    await flushPromises();

    expect(sidebarRows(wrapper)[0]?.state).toBe("Disabled");
  });

  it("says why the server refused a create, and keeps the form", async () => {
    refuseNext = { url: /^POST \/api\/automations$/, error: "Invalid cron expression: The given cron expression has an invalid format." };

    await createSchedule("Bad cron", "every monday");

    expect(wrapper.find("[role=alert]").text()).toContain("Invalid cron expression");
    expect(wrapper.find("#automation-name").exists()).toBe(true);
    expect(sidebarRows(wrapper).map((row) => row.name)).toEqual(["Weekly PR digest"]);
  });

  it("says why a switch-off failed", async () => {
    await wrapper.findAll(".automations-nav__item")[0]!.trigger("click");
    await flushPromises();
    refuseNext = { url: /\/disable$/, error: "Automation 'a1' not found." };

    await wrapper.find("button[role=switch]").trigger("click");
    await flushPromises();

    expect(wrapper.find("[data-testid=automation-action-message]").text()).toContain("Automation 'a1' not found.");
  });

  it("confirms that Run now started a run", async () => {
    await wrapper.findAll(".automations-nav__item")[0]!.trigger("click");
    await flushPromises();

    await wrapper.find("button[title='Run now']").trigger("click");
    await flushPromises();

    expect(wrapper.find("[data-testid=automation-action-message]").text()).toContain("Run started");
  });

  it("says which zone a schedule runs in, UTC for older ones", async () => {
    await wrapper.findAll(".automations-nav__item")[0]!.trigger("click");
    await flushPromises();

    expect(wrapper.find("[data-testid=automation-trigger-summary]").text()).toBe("0 9 * * 1 (UTC)");
  });

  it("names the event an event automation waits for", async () => {
    list = [{ ...weekly, id: "e1", name: "Review sub agents", triggerType: "event", triggerConfig: '{"eventType":"delegation.created"}' }];
    wrapper.unmount();
    wrapper = mount(Screen, { attachTo: document.body });
    await flushPromises();

    await wrapper.findAll(".automations-nav__item")[0]!.trigger("click");
    await flushPromises();

    expect(wrapper.find("[data-testid=automation-trigger-summary]").text()).toBe("A sub agent starts");
  });
});
