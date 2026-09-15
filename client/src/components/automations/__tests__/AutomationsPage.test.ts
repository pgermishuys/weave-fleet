import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h, ref, shallowRef } from "vue";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import type { ScannedRepository } from "@/api/client";
import type { Automation, AutomationRun } from "@/stores/automations";

const { apiFetchMock, navigate } = vi.hoisted(() => ({ apiFetchMock: vi.fn(), navigate: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));
vi.mock("@tanstack/vue-router", () => ({ useNavigate: () => navigate, useRouter: () => ({ navigate }) }));
vi.mock("@/lib/automations", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/automations")>()),
  // The person's clock is in Johannesburg; older automations were saved in UTC.
  browserTimeZone: () => "Africa/Johannesburg",
}));

const repositories = ref<ScannedRepository[]>([]);
vi.mock("@/composables/use-repositories", () => ({
  useRepositories: () => ({
    repositories,
    isLoading: shallowRef(false),
    error: shallowRef(null),
    scannedAt: shallowRef(1),
    refresh: vi.fn(),
  }),
}));
vi.mock("@/composables/use-repository-detail", () => ({
  useRepositoryDetail: () => ({ detail: shallowRef(null), isLoading: shallowRef(false), error: shallowRef(null) }),
}));

import AutomationDetailPanel from "../AutomationDetailPanel.vue";
import AutomationsNavPanel from "../AutomationsNavPanel.vue";
import { useAutomationsNav } from "@/composables/use-automations-nav";
import { NEW_SESSION_DEFAULTS_KEY } from "@/composables/use-new-session-defaults";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const fleet: ScannedRepository = { name: "weave-fleet", path: "/home/me/source/weave-fleet", parentRoot: "/home/me/source" };

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
  workspaceId: "/home/me/source/weave-fleet",
  model: null,
  agent: null,
  createdAt: "2026-09-01T09:00:00Z",
  updatedAt: null,
  targetType: "new_session",
  timeZone: "Africa/Johannesburg",
  isolation: "worktree",
  baseBranch: null,
  // The page shows times on the browser's clock, which is the test runner's: UTC.
  nextRunAt: "2026-09-21T09:00:00.0000000Z",
  lastRun: null,
};

function run(id: string, state: AutomationRun["state"], extra: Partial<AutomationRun> = {}): AutomationRun {
  return {
    id,
    automationId: "a1",
    trigger: "schedule",
    scheduledFor: "2026-09-14T07:00:00.0000000Z",
    startedAt: "2026-09-14T07:00:02.0000000Z",
    state,
    sessionId: state === "done" || state === "running" ? `session-${id}` : null,
    instanceId: null,
    error: null,
    ...extra,
  };
}

let list: Automation[] = [];
let runs: AutomationRun[] = [];
let refuseNext: { url: RegExp; error: string } | null = null;
const sent: { method: string; url: string; body: Record<string, unknown> }[] = [];

function serve(url: string, init?: RequestInit): Response {
  const method = init?.method ?? "GET";
  const body = init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : {};
  if (method !== "GET") sent.push({ method, url, body });
  if (refuseNext && refuseNext.url.test(`${method} ${url}`)) {
    const { error } = refuseNext;
    refuseNext = null;
    return json({ error }, 400);
  }
  if (method === "GET" && url === "/api/automations") return json({ automations: list });
  if (method === "GET" && url === "/api/automations/event-catalog") return json(["session_created", "delegation.created"]);
  if (method === "GET" && /\/api\/automations\/[^/]+\/runs$/.test(url)) return json({ runs });
  if (method === "GET" && url === "/api/automations/draft-from-session/s1") {
    return json({ prompt: "Go through the open pull requests and summarise each one.", folder: fleet.path, isolation: "existing" });
  }
  if (method === "POST" && url === "/api/automations") {
    const automation = { ...weekly, ...body, id: `a${list.length + 1}`, isEnabled: true, lastRun: null } as Automation;
    list = [...list, automation];
    return json(automation, 201);
  }
  const item = url.match(/^\/api\/automations\/([^/]+)(\/[a-z]+)?$/);
  if (method === "PUT" && item) {
    list = list.map((a) => (a.id === item[1] ? { ...a, ...body } as Automation : a));
    return json(list.find((a) => a.id === item[1]));
  }
  if (method === "POST" && item && (item[2] === "/enable" || item[2] === "/disable")) {
    list = list.map((a) => (a.id === item[1] ? { ...a, isEnabled: item[2] === "/enable" } : a));
    return new Response(null, { status: 204 });
  }
  if (method === "POST" && item?.[2] === "/run") {
    const started = run("r-now", "starting", { trigger: "manual", scheduledFor: null, startedAt: "2026-09-15T08:00:00.0000000Z" });
    runs = [started, ...runs];
    return json(started, 202);
  }
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
  return wrapper.findAll("[data-testid='automation-row']").map((row) => ({
    name: row.find(".automation-row__title").text(),
    status: row.find("[data-testid='automation-row-status']").exists() ? row.find("[data-testid='automation-row-status']").text() : "",
  }));
}

describe("Automations screen", () => {
  let wrapper: VueWrapper;

  beforeEach(async () => {
    // Tuesday 15 September 2026, 10:00 (the test runner's clock is UTC).
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date(2026, 8, 15, 10, 0, 0));
    setActivePinia(createPinia());
    localStorage.setItem(NEW_SESSION_DEFAULTS_KEY, JSON.stringify({
      lastFolder: { kind: "repository", path: fleet.path },
      recentFolders: [{ kind: "repository", path: fleet.path }],
      workspaceByRepository: {},
    }));
    repositories.value = [fleet];
    list = [weekly];
    runs = [];
    refuseNext = null;
    sent.length = 0;
    navigate.mockReset();
    apiFetchMock.mockReset();
    apiFetchMock.mockImplementation(async (url: string, init?: RequestInit) => serve(url, init));
    const nav = useAutomationsNav();
    nav.clearSelection();
    nav.resetDraft();
    wrapper = mount(Screen, { attachTo: document.body });
    await flushPromises();
  });

  afterEach(() => {
    wrapper.unmount();
    localStorage.clear();
    vi.useRealTimers();
  });

  async function startNew(text: string) {
    await wrapper.find("[data-testid='new-automation']").trigger("click");
    await flushPromises();
    await wrapper.find("[data-testid='automation-message']").setValue(text);
    await flushPromises();
  }

  it("reads when it runs out of the sentence, and creates it on, in a new worktree each run", async () => {
    await startNew("Every Monday at 9am, summarise the open PRs");

    expect(wrapper.find("[data-testid='automation-schedule-highlight']").text()).toBe("Every Monday at 9am");
    expect(wrapper.find("[data-testid='automation-when-chip']").text()).toContain("Mondays 09:00");
    const plan = wrapper.find("[data-testid='automation-plan']").text();
    expect(plan).toContain("Runs every Monday at 09:00 (Africa/Johannesburg time), next Mon 21 Sep, 09:00.");
    expect(plan).toContain("a new worktree of ~/source/weave-fleet");
    expect(plan).toContain("The agent gets “Summarise the open PRs”");
    expect(wrapper.find("[data-testid='automation-draft-row']").text()).toContain("Summarise the open PRs");

    await wrapper.find("[data-testid='automation-submit']").trigger("click");
    await flushPromises();

    expect(sent[0]).toMatchObject({
      method: "POST",
      url: "/api/automations",
      body: {
        name: "Summarise the open PRs",
        prompt: "Summarise the open PRs",
        triggerType: "schedule",
        triggerConfig: "0 9 * * 1",
        timeZone: "Africa/Johannesburg",
        workspaceId: fleet.path,
        isolation: "worktree",
        targetType: "new_session",
        maxConcurrentRuns: 1,
      },
    });
    expect(sidebarRows(wrapper).map((row) => row.name)).toEqual(["Weekly PR digest", "Summarise the open PRs"]);
    expect(wrapper.find("[data-testid='automation-title']").text()).toBe("Summarise the open PRs");
    expect(wrapper.find("[data-testid='automation-action-message']").text()).toContain("Created and on. First run");
  });

  it("stores “once on a monday” as that Monday's date, and leaves the words to Fleet", async () => {
    await startNew("Create an automation that runs once on a monday to check whether the release notes cover everything.");

    expect(wrapper.find("[data-testid='automation-when-chip']").text()).toContain("Once · Mon 21 Sep");
    await wrapper.find("[data-testid='automation-submit']").trigger("click");
    await flushPromises();

    expect(sent[0].body).toMatchObject({
      prompt: "Check whether the release notes cover everything.",
      triggerType: "once",
      triggerConfig: "2026-09-21T09:00",
    });
  });

  it("asks where it runs when no folder was used before, and opens the folder menu instead of creating", async () => {
    localStorage.clear();
    await startNew("Every Monday at 9am, summarise the open PRs");

    expect(wrapper.find("[data-testid='automation-plan']").text()).toContain("Choose where it runs.");
    await wrapper.find("[data-testid='automation-submit']").trigger("click");
    await flushPromises();

    expect(sent).toEqual([]);
  });

  it("asks when it should run, and won't create one without it", async () => {
    await startNew("Summarise the open PRs");

    expect(wrapper.find("[data-testid='automation-when-chip']").text()).toContain("When?");
    expect(wrapper.find("[data-testid='automation-plan']").text()).toContain("When should it run?");
    expect(wrapper.find("[data-testid='automation-submit']").attributes("disabled")).toBeDefined();
  });

  it("asks whether “on Friday” means every Friday, and can make it just once", async () => {
    await startNew("Tidy the changelog on Friday.");

    expect(wrapper.find("[data-testid='automation-plan']").text()).toContain("Every Friday, or just once?");
    await wrapper.find("[data-testid='automation-plan-ask']").trigger("click");
    await flushPromises();

    expect(wrapper.find("[data-testid='automation-when-chip']").text()).toContain("Once · Fri 18 Sep");
    expect(wrapper.find("[data-testid='automation-plan-ask']").text()).toBe("Every Friday instead");

    // "Just once" answered that sentence; a new one that says "every" repeats.
    await wrapper.find("[data-testid='automation-message']").setValue("Every Monday at 9am, summarise the open PRs");
    await flushPromises();
    expect(wrapper.find("[data-testid='automation-when-chip']").text()).toContain("Mondays 09:00");
    await wrapper.find("[data-testid='automation-message']").setValue("Tidy the changelog on Friday.");
    await flushPromises();
    expect(wrapper.find("[data-testid='automation-plan']").text()).toContain("Every Friday, or just once?");
  });

  it("says why the server refused a create, and keeps the text", async () => {
    refuseNext = { url: /^POST \/api\/automations$/, error: "That time has already passed. Pick a later one." };
    await startNew("Every Monday at 9am, summarise the open PRs");

    await wrapper.find("[data-testid='automation-submit']").trigger("click");
    await flushPromises();

    expect(wrapper.find("[data-testid='automation-form-error']").text()).toContain("That time has already passed");
    expect((wrapper.find("[data-testid='automation-message']").element as HTMLTextAreaElement).value)
      .toBe("Every Monday at 9am, summarise the open PRs");
  });

  it("shows each automation's next run, Running, Failed or Off in the sidebar, like session rows", async () => {
    list = [
      weekly,
      { ...weekly, id: "a2", name: "Nightly dependency check", lastRun: run("x", "running") },
      { ...weekly, id: "a3", name: "Tidy stale worktrees", lastRun: run("y", "failed", { error: "Couldn't start" }) },
      { ...weekly, id: "a4", name: "Old digest", isEnabled: false, nextRunAt: null },
    ];
    await useAutomationsNavRefresh();

    expect(sidebarRows(wrapper)).toEqual([
      { name: "Weekly PR digest", status: "Mon 09:00" },
      { name: "Nightly dependency check", status: "Running" },
      { name: "Tidy stale worktrees", status: "Failed" },
      { name: "Old digest", status: "Off" },
    ]);
  });

  it("lists an automation's runs, and Run now adds one straight away", async () => {
    runs = [
      run("r2", "skipped", { error: "Skipped: Fleet wasn't running at Mon 14 Sep, 09:00, and it was more than 3 hours late when Fleet started." }),
      run("r1", "done", { scheduledFor: "2026-09-07T07:00:00.0000000Z" }),
    ];
    await openAutomation("a1");

    const rows = () => wrapper.findAll("[data-testid='automation-run']").map((row) => row.text());
    expect(rows()[0]).toContain("Skipped");
    expect(rows()[0]).toContain("Fleet wasn't running");
    expect(rows()[1]).toContain("Done");

    await wrapper.find("[data-testid='automation-run-now']").trigger("click");
    await flushPromises();

    expect(rows()[0]).toContain("Run now");
    expect(rows()[0]).toContain("Starting");
    expect(wrapper.find("[data-testid='automation-action-message']").text()).toContain("Started a run");
  });

  it("opens the session a run started", async () => {
    runs = [run("r1", "done")];
    await openAutomation("a1");

    await wrapper.find("[data-testid='automation-run']").trigger("click");

    expect(navigate).toHaveBeenCalledWith(expect.objectContaining({ to: "/sessions/$id", params: { id: "session-r1" } }));
  });

  it("offers Save only once something changed, and keeps the schedule it had", async () => {
    // A prompt as someone typed it, lower case and all, isn't a change.
    list = [{ ...weekly, prompt: "summarise the open PRs:" }];
    await openAutomation("a1");
    expect(wrapper.find("[data-testid='automation-submit']").exists()).toBe(false);

    await wrapper.find("[data-testid='automation-message']").setValue("Summarise the open PRs and the drafts");
    await flushPromises();
    await wrapper.find("[data-testid='automation-submit']").trigger("click");
    await flushPromises();

    expect(sent[0]).toMatchObject({
      method: "PUT",
      url: "/api/automations/a1",
      body: { prompt: "Summarise the open PRs and the drafts", triggerType: "schedule", triggerConfig: "0 9 * * 1", isolation: "worktree" },
    });
  });

  it("warns that saving moves an older UTC schedule to your time zone", async () => {
    list = [{ ...weekly, timeZone: null }];
    await openAutomation("a1");

    expect(wrapper.find("[data-testid='automation-plan']").text()).toContain("Saving moves it from UTC time to Africa/Johannesburg.");
  });

  it("shows a switch-off in the sidebar too", async () => {
    await openAutomation("a1");

    await wrapper.find("[data-testid='automation-enabled']").trigger("click");
    await flushPromises();

    expect(sidebarRows(wrapper)[0].status).toBe("Off");
    expect(wrapper.find("[data-testid='automation-header-meta']").text()).toBe("Off. It won't run until you switch it on.");
  });

  it("says why a switch-off failed", async () => {
    refuseNext = { url: /disable$/, error: "Automation 'a1' was not found." };
    await openAutomation("a1");

    await wrapper.find("[data-testid='automation-enabled']").trigger("click");
    await flushPromises();

    expect(wrapper.find("[data-testid='automation-action-message']").text()).toContain("Automation 'a1' was not found.");
  });

  it("starts “Repeat on a schedule…” from the session's first message and folder", async () => {
    useAutomationsNav().startCreateFromSession("s1");
    await flushPromises();

    expect((wrapper.find("[data-testid='automation-message']").element as HTMLTextAreaElement).value)
      .toBe("Go through the open pull requests and summarise each one.");
    const plan = wrapper.find("[data-testid='automation-plan']").text();
    expect(plan).toContain("When should it run?");
    expect(plan).toContain("in ~/source/weave-fleet as it is.");
  });

  async function openAutomation(id: string) {
    await useAutomationsNavRefresh();
    const index = list.findIndex((a) => a.id === id);
    await wrapper.findAll("[data-testid='automation-row']")[index].trigger("click");
    await flushPromises();
  }

  async function useAutomationsNavRefresh() {
    const { useAutomationsStore } = await import("@/stores/automations");
    await useAutomationsStore().refresh();
    await flushPromises();
  }
});
