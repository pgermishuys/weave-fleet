import { flushPromises, mount } from "@vue/test-utils";
import { nextTick } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { SessionListItem } from "@/api/client";
import GitHubWorkItemDetailPage from "@/components/pages/GitHubWorkItemDetailPage.vue";
import type { SmartLinkWire } from "@/lib/smart-links";
import { useSessionsStore } from "@/stores/sessions";
import { useSidebarStore } from "@/stores/sidebar";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";

const { apiFetchMock, mockNavigate } = vi.hoisted(() => ({
  apiFetchMock: vi.fn(),
  mockNavigate: vi.fn(),
}));

vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));
// The sessions list comes from the store the test fills.
vi.mock("@/composables/use-sessions", () => ({ useSessions: () => ({}) }));
vi.mock("@tanstack/vue-router", () => ({
  useRouter: () => ({ navigate: mockNavigate }),
  useLocation: () => ({ pathname: { value: "/" } }),
}));

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

function summary(overrides: Record<string, unknown> = {}) {
  return {
    kind: "issue",
    owner: "acme",
    repo: "weave",
    number: 42,
    title: "Investigate missing GitHub context",
    url: "https://github.com/acme/weave/issues/42",
    state: "open",
    isDraft: false,
    author: "octocat",
    authorAvatarUrl: null,
    createdAt: "2026-04-01T00:00:00Z",
    updatedAt: "2026-04-02T00:00:00Z",
    comments: 1,
    labels: [{ name: "bug", color: "d73a4a" }],
    headRef: null,
    baseRef: null,
    additions: null,
    deletions: null,
    checks: null,
    reviewDecision: null,
    mergeable: null,
    reviewers: [],
    assignees: ["octocat"],
    ...overrides,
  };
}

function detail(overrides: Record<string, unknown> = {}, summaryOverrides: Record<string, unknown> = {}) {
  return {
    summary: summary(summaryOverrides),
    body: "## Repro\n\n- [x] opened it",
    changedFiles: null,
    mergedAt: null,
    mergedBy: null,
    checks: [],
    unresolvedThreads: 0,
    timeline: [
      { kind: "comment", author: "tvdb", authorAvatarUrl: null, createdAt: "2026-04-01T02:00:00Z", body: "Same here", state: null, url: null, reference: null },
    ],
    ...overrides,
  };
}

function session(id: string): SessionListItem {
  return {
    instanceId: `i-${id}`,
    workspaceId: "w",
    workspaceDirectory: "/repo",
    workspaceDisplayName: null,
    isolationStrategy: "worktree",
    sessionStatus: "idle",
    session: { id, title: "Implement server-side queue", time: { created: 1, updated: 2 } },
    instanceStatus: "running",
    lifecycleStatus: "running",
    retentionStatus: "active",
    typedInstanceStatus: "running",
    isHidden: false,
  } as unknown as SessionListItem;
}

function mountPage(props: { kind: "issue" | "pull"; number: string }) {
  return mount(GitHubWorkItemDetailPage, {
    props: { owner: "acme", repo: "weave", ...props },
    attachTo: document.body,
    global: { stubs: { teleport: false, MarkdownRenderer: { props: ["content"], template: "<div class=\"md\">{{ content }}</div>" } } },
  });
}

describe("GitHubWorkItemDetailPage", () => {
  beforeEach(() => {
    apiFetchMock.mockReset();
    mockNavigate.mockReset();
    apiFetchMock.mockImplementation(async (url: string) => {
      if (url === "/api/smart-links") return json([]);
      if (url.endsWith("/items/42")) return json(detail());
      return json({}, 404);
    });
  });

  it("waits for the sessions panel switch before setting the preset and navigating", async () => {
    const sidebarStore = useSidebarStore();
    const workspaceUiStore = useWorkspaceUiStore();
    const setNewSessionInitialSourceSpy = vi.spyOn(workspaceUiStore, "setNewSessionInitialSource");

    const wrapper = mountPage({ kind: "issue", number: "42" });
    await flushPromises();

    const primary = wrapper.get('[data-testid="github-item-primary"]');
    expect(primary.text()).toBe("Start a session");
    (primary.element as HTMLButtonElement).click();

    expect(sidebarStore.panelCollapsed).toBe(false);
    expect(sidebarStore.activeRail).toBe("sessions");
    expect(setNewSessionInitialSourceSpy).not.toHaveBeenCalled();
    expect(mockNavigate).not.toHaveBeenCalled();

    await nextTick();

    expect(setNewSessionInitialSourceSpy).toHaveBeenCalledWith(expect.objectContaining({
      kind: "github",
      sourceType: "github-issue",
      owner: "acme",
      repo: "weave",
      number: 42,
      title: "Investigate missing GitHub context",
      body: "## Repro\n\n- [x] opened it",
      htmlUrl: "https://github.com/acme/weave/issues/42",
      repoFullName: "acme/weave",
    }));
    expect(mockNavigate).toHaveBeenCalledWith({ to: "/sessions/new", search: { projectId: undefined, source: undefined } });
    wrapper.unmount();
  });

  it("shows the conversation, labels and assignees of an issue", async () => {
    const wrapper = mountPage({ kind: "issue", number: "42" });
    await flushPromises();

    expect(wrapper.text()).toContain("octocat opened this");
    expect(wrapper.text()).toContain("tvdb");
    expect(wrapper.text()).toContain("commented");
    expect(wrapper.text()).toContain("Same here");
    expect(wrapper.text()).toContain("No session is working on this yet.");
    wrapper.unmount();
  });

  it("sends a pull request's failing checks to the session working on it", async () => {
    const link: SmartLinkWire = {
      id: "l1",
      sessionId: "s1",
      url: "https://github.com/acme/weave/pull/7",
      providerId: "github",
      resourceType: "pull_request",
      resourceId: "acme/weave#7",
      title: "acme/weave #7: Queue",
      status: "open",
      statusLabel: "Open",
      metadataJson: JSON.stringify({
        owner: "acme",
        repo: "weave",
        number: 7,
        ci: { headSha: "abc", ciStatus: "failure", checkRuns: [{ id: 1, name: "client-tests", status: "completed", conclusion: "failure", htmlUrl: "", workflowName: null, startedAt: null, completedAt: null }] },
      }),
      isDismissed: false,
      isTerminal: false,
      createdAt: "",
      updatedAt: "",
      relationship: "own",
      enrichmentStatus: "resolved",
      lastCheckedAt: null,
    };
    apiFetchMock.mockImplementation(async (url: string) => {
      if (url === "/api/smart-links") return json([link]);
      if (url.endsWith("/items/7")) {
        return json(detail({
          changedFiles: 14,
          checks: [
            { name: "client-tests", state: "failure", url: "https://ci/1", workflowName: "CI", checkRunId: 1, startedAt: null, completedAt: null },
            { name: "lint", state: "success", url: null, workflowName: "CI", checkRunId: 2, startedAt: null, completedAt: null },
          ],
        }, {
          kind: "pull",
          number: 7,
          title: "Queue",
          url: "https://github.com/acme/weave/pull/7",
          headRef: "feat/queue",
          baseRef: "main",
          additions: 412,
          deletions: 88,
          checks: "failure",
          reviewers: [{ login: "tvdb", avatarUrl: null, state: "APPROVED" }],
        }));
      }
      return json({ ok: true });
    });
    useSessionsStore().setSessions([session("s1")]);

    const wrapper = mountPage({ kind: "pull", number: "7" });
    await flushPromises();

    expect(wrapper.text()).toContain("wants to merge");
    expect(wrapper.text()).toContain("in 14 files");
    expect(wrapper.get('[data-testid="github-item-session"]').text()).toContain("Implement server-side queue");
    expect(wrapper.text()).toContain("Some checks were not successful");
    expect(wrapper.text()).toContain("Approved");

    const primary = wrapper.get('[data-testid="github-item-primary"]');
    expect(primary.text()).toBe("Fix failing checks");
    await primary.trigger("click");
    await flushPromises();

    const call = apiFetchMock.mock.calls.find(([url]) => url === "/api/sessions/s1/prompt");
    expect(JSON.parse(String((call?.[1] as RequestInit).body)).text).toContain("Workflow: client-tests");
    expect(wrapper.get('[data-testid="github-item-primary"]').text()).toBe("Sent to the session");
    wrapper.unmount();
  });

  it("says why it couldn't load", async () => {
    apiFetchMock.mockImplementation(async (url: string) =>
      url === "/api/smart-links" ? json([]) : json({ error: "Could not resolve to an issue or pull request." }, 404));

    const wrapper = mountPage({ kind: "issue", number: "42" });
    await flushPromises();

    expect(wrapper.get('[role="alert"]').text()).toContain("Could not resolve to an issue or pull request.");
    wrapper.unmount();
  });
});
