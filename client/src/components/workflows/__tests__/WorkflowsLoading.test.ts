// While the Workflows page is still asking, its list and its Recent runs say they're loading, not that there's
// nothing: the repo part used to say "Pick a repository" for the one just picked, and Recent runs "No runs yet".
import { flushPromises, mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({
  apiFetch: apiFetchMock,
  apiFetchOn: (_machine: unknown, ...args: unknown[]) => apiFetchMock(...args),
}));
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }), useNavigate: () => vi.fn() }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: () => () => {}, onDomainEvent: () => () => {} }));

import WorkflowsNavPanel from "@/components/workflows/WorkflowsNavPanel.vue";
import WorkflowDetailPanel from "@/components/workflows/WorkflowDetailPanel.vue";
import { useWorkflowsNav } from "@/composables/use-workflows-nav";
import { useWorkflowsStore } from "@/stores/workflows";
import { liveTarget } from "@/lib/machine-target";

const json = (body: unknown) => new Response(JSON.stringify(body), { headers: { "Content-Type": "application/json" } });

describe("the Workflows page while it loads", () => {
  it("shows the repo's workflows loading, not “Pick a repository”, until the picked repo answers", async () => {
    let answerRepo: (() => void) | null = null;
    apiFetchMock.mockImplementation((path: string) => {
      if (path === "/api/workflows") return Promise.resolve(json({ repository: null, repositoryName: null, workflows: [] }));
      if (path.startsWith("/api/workflows?directory=")) {
        return new Promise<Response>((resolve) => {
          answerRepo = () => resolve(json({ repository: "/work/kettle", repositoryName: "kettle", workflows: [] }));
        });
      }
      return Promise.resolve(json([]));
    });
    const wrapper = mount(WorkflowsNavPanel);
    await flushPromises();
    expect(wrapper.text()).toContain("Pick a repository in the Run box to see its workflows");

    useWorkflowsNav().setFolder({ kind: "repository", path: "/work/kettle" }, true);
    await flushPromises();

    expect(wrapper.text()).not.toContain("Pick a repository");
    expect(wrapper.text()).not.toContain("No workflow files in this repo yet");
    expect(wrapper.find("[data-testid='workflows-repo-loading']").exists()).toBe(true);

    answerRepo!();
    await flushPromises();

    expect(wrapper.find("[data-testid='workflows-repo-loading']").exists()).toBe(false);
    expect(wrapper.text()).toContain("kettle · .weave/workflows");
    expect(wrapper.text()).toContain("No workflow files in this repo yet");
  });

  it("says the recent runs are loading, not “No runs yet”, until they've been asked for", async () => {
    const fixABug = {
      id: "builtin:fix-a-bug", builtIn: true, file: null, name: "Fix a bug", description: null, placeholder: null,
      startsFrom: null, runsIn: null, errors: [],
      steps: [{ id: "fix", title: "Fix", kind: "agent", agent: null, model: null, effort: null, skill: null, optional: false,
        optionalHint: null, outcomes: [], routes: {}, maxLoops: null, ask: null }],
    };
    let answerRuns: (() => void) | null = null;
    apiFetchMock.mockImplementation((path: string) => {
      if (path.startsWith("/api/workflows/runs")) return new Promise<Response>((resolve) => { answerRuns = () => resolve(json([])); });
      if (path.startsWith("/api/workflows")) return Promise.resolve(json({ repository: null, repositoryName: null, workflows: [fixABug] }));
      return Promise.resolve(json([]));
    });
    const nav = useWorkflowsNav();
    await nav.reload();
    nav.setActiveWorkflow("builtin:fix-a-bug");
    void useWorkflowsStore(liveTarget()).ensureLoaded();
    const wrapper = mount(WorkflowDetailPanel, {
      global: { stubs: { WorkflowRunBox: true, WorkflowEditor: true, WorkflowCreateDialog: true, WorkflowStartedBy: true } },
    });
    await flushPromises();

    expect(wrapper.text()).toContain("Recent runs");
    expect(wrapper.text()).not.toContain("No runs yet");
    expect(wrapper.find("[data-testid='workflow-runs-loading']").exists()).toBe(true);

    answerRuns!();
    await flushPromises();

    expect(wrapper.find("[data-testid='workflow-runs-loading']").exists()).toBe(false);
    expect(wrapper.text()).toContain("No runs yet");
  });
});
