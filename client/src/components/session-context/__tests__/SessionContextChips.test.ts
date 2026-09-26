import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { SmartLinkWire } from "@/lib/smart-links";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));

import SessionContextChips from "@/components/session-context/SessionContextChips.vue";
import { useCanvasesStore } from "@/stores/canvases";
import { useSidebarStore } from "@/stores/sidebar";
import { useSmartLinksStore } from "@/stores/smart-links";

function wire(id: string, relationship: string, metadata: Record<string, unknown> = {}, overrides: Partial<SmartLinkWire> = {}): SmartLinkWire {
  return {
    id,
    sessionId: "s1",
    url: `https://github.com/o/r/pull/${id}`,
    providerId: "github",
    resourceType: "pull_request",
    resourceId: `o/r#${id}`,
    title: `o/r #${id}: Link ${id}`,
    status: "open",
    statusLabel: "Open",
    metadataJson: JSON.stringify(metadata),
    isDismissed: false,
    isTerminal: false,
    createdAt: "",
    updatedAt: "",
    relationship,
    enrichmentStatus: "resolved",
    lastCheckedAt: null,
    ...overrides,
  };
}

const failingChecks = {
  ci: { headSha: "abc", ciStatus: "failure", checkRuns: [{ id: 1, name: "tests", status: "completed", conclusion: "failure", htmlUrl: "", workflowName: null, startedAt: null, completedAt: null }] },
  reviewThreads: { unresolvedCount: 2, threads: [
    { threadNodeId: "t1", isResolved: false, isOutdated: false, path: "a.ts", line: 1, comments: [] },
    { threadNodeId: "t2", isResolved: false, isOutdated: false, path: "b.ts", line: 2, comments: [] },
  ] },
};

describe("SessionContextChips", () => {
  // test-setup.ts installs a fresh Pinia per test for both the test and mounted components.
  beforeEach(() => {
    localStorage.clear();
    apiFetchMock.mockReset();
    apiFetchMock.mockImplementation(() => Promise.resolve(new Response("[]", { status: 200 })));
  });

  it("shows where the session came from, its pull request as a pill, and pinned links as chips", async () => {
    useSmartLinksStore().setLinks("s1", [
      wire("42", "origin", {}, { resourceType: "issue", url: "https://github.com/o/r/issues/42" }),
      wire("187", "own", failingChecks),
      wire("9", "pinned"),
      wire("7", "mentioned"),
    ]);

    const wrapper = mount(SessionContextChips, { props: { sessionId: "s1" } });
    await flushPromises();

    expect(wrapper.get('[data-testid="context-chip-42"]').text()).toBe("from #42");
    const pill = wrapper.get('[data-testid="session-pr-pill"]');
    expect(pill.attributes("data-pr")).toBe("blocked");
    expect(pill.text()).toContain("#187");
    expect(pill.text()).toContain("1 failing · 2 threads");
    expect(wrapper.find('[data-testid="context-chip-9"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="context-chip-7"]').exists()).toBe(false);
  });

  it("opens the Context tab at the origin when clicked", async () => {
    useSmartLinksStore().setLinks("s1", [wire("42", "origin", {}, { resourceType: "issue", url: "https://github.com/o/r/issues/42" })]);
    const sidebar = useSidebarStore();
    sidebar.setRightPanelCollapsed(true);

    const wrapper = mount(SessionContextChips, { props: { sessionId: "s1" } });
    await flushPromises();
    await wrapper.get('[data-testid="context-chip-42"]').trigger("click");

    expect(sidebar.rightPanelCollapsed).toBe(false);
    expect(useCanvasesStore().sessionCanvases("s1").activeId).toBe("context");
    expect(useSmartLinksStore().focusRequest).toMatchObject({ sessionId: "s1", target: "42" });
  });

  it("sends every failing check and open thread to the agent from the pill", async () => {
    useSmartLinksStore().setLinks("s1", [wire("187", "own", failingChecks)]);
    const wrapper = mount(SessionContextChips, { props: { sessionId: "s1" }, attachTo: document.body, global: { stubs: { teleport: false } } });
    await flushPromises();

    await wrapper.get('[data-testid="session-pr-pill"]').trigger("click");
    await flushPromises();
    const fix = document.querySelector<HTMLButtonElement>('[data-testid="session-pr-fix"]');
    expect(fix?.textContent).toContain("Fix checks and address review");

    fix!.click();
    await flushPromises();

    const [url, init] = apiFetchMock.mock.calls.find(([path]) => String(path).endsWith("/prompt"))!;
    expect(url).toBe("/api/sessions/s1/prompt");
    const text = JSON.parse(String((init as RequestInit).body)).text as string;
    expect(text).toContain("Workflow: tests");
    expect(text).toContain("File: a.ts:1");
    expect(text).toContain("File: b.ts:2");
    expect(document.querySelector('[data-testid="session-pr-fix"]')?.textContent).toContain("Sent to the agent");
    wrapper.unmount();
  });

  it("offers to archive the session once its pull request is merged", async () => {
    useSmartLinksStore().setLinks("s1", [wire("187", "own", {}, { status: "merged", statusLabel: "Merged", isTerminal: true })]);
    const wrapper = mount(SessionContextChips, { props: { sessionId: "s1" }, attachTo: document.body, global: { stubs: { teleport: false } } });
    await flushPromises();

    const pill = wrapper.get('[data-testid="session-pr-pill"]');
    expect(pill.attributes("data-pr")).toBe("merged");
    await pill.trigger("click");
    await flushPromises();

    expect(document.querySelector('[data-testid="session-pr-popover"]')?.textContent).toContain("Archive session");
    wrapper.unmount();
  });

  it("names the automation a session was started by", async () => {
    const wrapper = mount(SessionContextChips, {
      props: {
        sessionId: "s1",
        origin: { sourceType: "automation", title: "Nightly dependency sweep", resourceUrl: null, resourceId: "auto-1", providerId: "builtin.automation" },
      },
    });
    await flushPromises();

    expect(wrapper.get(".context-chip").text()).toContain("Nightly dependency sweep");
  });

  it("renders nothing when the session has no context", async () => {
    const wrapper = mount(SessionContextChips, { props: { sessionId: "s1" } });
    await flushPromises();

    expect(wrapper.find(".context-chips").exists()).toBe(false);
  });
});
