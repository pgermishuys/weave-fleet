import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h, ref } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import ProblemReportDialog from "@/components/report/ProblemReportDialog.vue";
import type { PrepareReportRequest, PreparedReport } from "@/lib/problem-report";
import { useProblemReportStore } from "@/stores/problem-report";
import { useSessionsStore } from "@/stores/sessions";
import type { SessionListItem } from "@/api/client";

const { apiFetch, requests } = vi.hoisted(() => ({
  apiFetch: vi.fn(),
  requests: [] as { path: string; body: unknown }[],
}));

vi.mock("@/lib/api-client", () => ({ apiFetch }));
vi.mock("@tanstack/vue-router", () => ({ useLocation: () => ref("/sessions/s1") }));
vi.mock("@/lib/problem-report", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/problem-report")>()),
  captureScreenshot: vi.fn(async () => ({
    dataUrl: "data:image/png;base64,iVBORw0KGgo=",
    width: 1440,
    height: 900,
    takenAt: new Date("2026-09-27T14:03:10Z"),
  })),
}));

// reka-ui renders dialog content through a portal; show it in place.
vi.mock("@/components/ui/dialog", () => {
  const pass = (name: string) => defineComponent({ name, setup: (_p, { slots }) => () => h("div", slots.default?.()) });
  return {
    Dialog: defineComponent({ name: "DialogStub", props: { open: Boolean }, setup: (props, { slots }) => () => (props.open ? h("div", slots.default?.()) : null) }),
    DialogContent: pass("DialogContent"),
    DialogTitle: pass("DialogTitle"),
    DialogDescription: pass("DialogDescription"),
  };
});

const PREPARED: PreparedReport = {
  title: "Stopped updating in ‹folder-1›",
  body: "## What happened\nStopped updating in ‹folder-1›.\n",
  log: "2026-09-27 14:02:03.911 [WRN] [SessionHub] aborted for ‹user›\n",
  logEntries: 1,
  labels: ["opencode2", "desktop"],
  replacements: [
    { label: "‹folder-1›", kind: "folder", shown: "/work/acme-payments" },
    { label: "‹user›", kind: "user", shown: "sam" },
  ],
  problems: [],
  canSend: true,
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function session(): SessionListItem {
  return {
    instanceId: "i1",
    workspaceId: "w1",
    workspaceDirectory: "/work/acme-payments",
    workspaceDisplayName: null,
    isolationStrategy: "existing",
    sessionStatus: "active",
    session: { id: "s1", title: "Fix invoice rounding", time: { created: 0, updated: 0 } },
    instanceStatus: "running",
    lifecycleStatus: "running",
    retentionStatus: "active",
    typedInstanceStatus: "running",
    isHidden: false,
    tags: [],
  } as unknown as SessionListItem;
}

async function opened(options: { description?: string } = {}) {
  const wrapper = mount(ProblemReportDialog, { attachTo: document.body });
  const sessions = useSessionsStore();
  sessions.setSessions([session()]);
  sessions.setActiveSessionId("s1");
  await useProblemReportStore().show({ from: "help", description: options.description });
  await flushPromises();
  return wrapper;
}

describe("ProblemReportDialog", () => {
  beforeEach(() => {
    requests.length = 0;
    apiFetch.mockReset();
    apiFetch.mockImplementation(async (path: string, init?: RequestInit) => {
      const body = init?.body ? JSON.parse(init.body as string) : null;
      requests.push({ path, body });
      if (path === "/api/reports/prepare") return json(PREPARED);
      if (path === "/api/reports/send") return json({ id: "r_abc234" });
      return json({ error: "unexpected" }, 500);
    });
  });

  it("opens on Describe with the screenshot and every part ticked, about the open session", async () => {
    const wrapper = await opened();

    expect(wrapper.text()).toContain("Nothing leaves your machine until you send it.");
    expect(wrapper.find("img.report__thumb").attributes("src")).toContain("data:image/png");
    expect(wrapper.text()).toContain("5 of 5");
    expect(wrapper.text()).toContain("About: Fix invoice rounding");
    expect(wrapper.get('[data-testid="problem-report-review"]').attributes("disabled")).toBeDefined();
    wrapper.unmount();
  });

  it("starts the description with the text it was opened with", async () => {
    const wrapper = await opened({ description: "This turn stopped early: overloaded\n\n" });

    expect((wrapper.get('[data-testid="problem-report-description"]').element as HTMLTextAreaElement).value)
      .toBe("This turn stopped early: overloaded\n\n");
    wrapper.unmount();
  });

  it("asks the server to prepare only the ticked parts, with what the window knows", async () => {
    const wrapper = await opened();
    await wrapper.get('[data-testid="problem-report-description"]').setValue("Stopped updating in /work/acme-payments.");
    await wrapper.get('[data-testid="problem-report-include-log"]').setValue(false);
    await wrapper.get('[data-testid="problem-report-review"]').trigger("click");
    await flushPromises();

    const prepare = requests.find((r) => r.path === "/api/reports/prepare")!.body as PrepareReportRequest;
    expect(prepare.kind).toBe("bug");
    expect(prepare.sessionId).toBe("s1");
    expect(prepare.include).toEqual({ environment: true, where: true, connection: true, log: false });
    expect(prepare.client.screen).toBe("Sessions");
    expect(prepare.client.connection[0].name).toBe("Live updates");
    expect(prepare.client.privateValues).toContainEqual({ kind: "url", value: window.location.origin });
    wrapper.unmount();
  });

  it("shows the prepared report with labels, and what they replaced on request", async () => {
    const wrapper = await opened();
    await wrapper.get('[data-testid="problem-report-description"]').setValue("Stopped updating.");
    await wrapper.get('[data-testid="problem-report-review"]').trigger("click");
    await flushPromises();

    expect(wrapper.text()).toContain("2 private details replaced");
    expect(wrapper.text()).toContain("1 folder, 1 user name");
    expect(wrapper.findAll(".report__label").map((label) => label.text())).toEqual(["‹folder-1›"]);
    expect(wrapper.find(".report__was").exists()).toBe(false);

    await wrapper.get('[data-testid="problem-report-show-removed"]').trigger("click");
    expect(wrapper.get(".report__was").text()).toBe("/work/acme-payments");

    await wrapper.get('[data-testid="problem-report-file-log"]').trigger("click");
    expect(wrapper.get('[data-testid="problem-report-preview"]').text()).toContain("aborted for sam‹user›");
    wrapper.unmount();
  });

  it("sends the reviewed report with the screenshot, title and contact, then shows its id", async () => {
    const wrapper = await opened();
    await wrapper.get('[data-testid="problem-report-description"]').setValue("Stopped updating.");
    await wrapper.get('[data-testid="problem-report-contact"]').setValue("@sam");
    await wrapper.get('[data-testid="problem-report-review"]').trigger("click");
    await flushPromises();
    await wrapper.get('[data-testid="problem-report-title"]').setValue("Conversation stops after sleep");
    await wrapper.get('[data-testid="problem-report-send"]').trigger("click");
    await flushPromises();

    const sent = requests.find((r) => r.path === "/api/reports/send")!.body as Record<string, unknown>;
    expect(sent).toEqual({
      kind: "bug",
      title: "Conversation stops after sleep",
      body: PREPARED.body,
      log: PREPARED.log,
      screenshot: "data:image/png;base64,iVBORw0KGgo=",
      contact: "@sam",
      labels: ["opencode2", "desktop"],
    });
    expect(wrapper.get('[data-testid="problem-report-sent"]').text()).toContain("r_abc234");
    expect(wrapper.text()).toContain("a reply goes to @sam");
    wrapper.unmount();
  });

  it("leaves the screenshot out once it's removed on the review screen", async () => {
    const wrapper = await opened();
    await wrapper.get('[data-testid="problem-report-description"]').setValue("Stopped updating.");
    await wrapper.get('[data-testid="problem-report-review"]').trigger("click");
    await flushPromises();
    await wrapper.get('[data-testid="problem-report-file-screenshot"]').trigger("click");
    await wrapper.get('[data-testid="problem-report-remove-screenshot"]').trigger("click");

    expect(wrapper.find('[data-testid="problem-report-file-screenshot"]').exists()).toBe(false);
    await wrapper.get('[data-testid="problem-report-send"]').trigger("click");
    await flushPromises();
    expect((requests.find((r) => r.path === "/api/reports/send")!.body as { screenshot: unknown }).screenshot).toBeNull();
    wrapper.unmount();
  });

  it("keeps the report open and says why when sending fails", async () => {
    apiFetch.mockImplementation(async (path: string) =>
      path === "/api/reports/prepare"
        ? json(PREPARED)
        : json({ error: "Too many reports from this address. Try again in a few minutes." }, 429));
    const wrapper = await opened();
    await wrapper.get('[data-testid="problem-report-description"]').setValue("Stopped updating.");
    await wrapper.get('[data-testid="problem-report-review"]').trigger("click");
    await flushPromises();
    await wrapper.get('[data-testid="problem-report-send"]').trigger("click");
    await flushPromises();

    expect(wrapper.get('[role="alert"]').text()).toBe("Too many reports from this address. Try again in a few minutes.");
    expect(wrapper.find('[data-testid="problem-report-send"]').exists()).toBe(true);
    wrapper.unmount();
  });

  it("turns Send off when this Fleet has no inbox", async () => {
    apiFetch.mockImplementation(async () => json({ ...PREPARED, canSend: false }));
    const wrapper = await opened();
    await wrapper.get('[data-testid="problem-report-description"]').setValue("Stopped updating.");
    await wrapper.get('[data-testid="problem-report-review"]').trigger("click");
    await flushPromises();

    expect(wrapper.get('[data-testid="problem-report-send"]').attributes("disabled")).toBeDefined();
    expect(wrapper.text()).toContain("Save it as a file instead");
    wrapper.unmount();
  });
});
