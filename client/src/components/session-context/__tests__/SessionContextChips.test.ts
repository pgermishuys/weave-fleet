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

  it("shows a chip for origin, own and pinned links but not mentions", async () => {
    useSmartLinksStore().setLinks("s1", [
      wire("42", "origin", {}, { resourceType: "issue", url: "https://github.com/o/r/issues/42" }),
      wire("187", "own", failingChecks),
      wire("7", "mentioned"),
    ]);

    const wrapper = mount(SessionContextChips, { props: { sessionId: "s1" } });
    await flushPromises();

    const chips = wrapper.findAll(".context-chip");
    expect(chips.map((chip) => chip.find(".context-chip__number").text())).toEqual(["#42", "#187"]);

    const pr = wrapper.get('[data-testid="context-chip-187"]');
    expect(pr.classes()).toContain("context-chip--attention");
    expect(pr.attributes("aria-label")).toContain("Checks: 1 failing");
    expect(pr.attributes("aria-label")).toContain("2 unresolved review threads");
  });

  it("opens the Context tab at the link when clicked", async () => {
    useSmartLinksStore().setLinks("s1", [wire("187", "own")]);
    const sidebar = useSidebarStore();
    sidebar.setRightPanelCollapsed(true);

    const wrapper = mount(SessionContextChips, { props: { sessionId: "s1" } });
    await flushPromises();
    await wrapper.get('[data-testid="context-chip-187"]').trigger("click");

    expect(sidebar.rightPanelCollapsed).toBe(false);
    expect(useCanvasesStore().sessionCanvases("s1").activeId).toBe("context");
    expect(useSmartLinksStore().focusRequest).toMatchObject({ sessionId: "s1", target: "187" });
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
