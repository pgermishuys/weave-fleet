import { flushPromises, mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import { defineComponent, h } from "vue";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: () => () => {} }));

import { useWorkflowsNav } from "@/composables/use-workflows-nav";

describe("useWorkflowsNav", () => {
  it("keeps following the Run box's repository after the component that asked first is gone", async () => {
    apiFetchMock.mockImplementation(() => Promise.resolve(new Response(JSON.stringify({ repository: "/work/repo", repositoryName: "repo", workflows: [] }))));
    // A session row can be the first to ask (Save as workflow…), and the Sessions page unmounts on the way to Workflows.
    const row = mount(defineComponent({ setup: () => { useWorkflowsNav(); return () => h("div"); } }));
    row.unmount();

    useWorkflowsNav().setFolder({ kind: "repository", path: "/work/repo" }, true);
    await flushPromises();

    expect(apiFetchMock).toHaveBeenCalledWith("/api/workflows?directory=%2Fwork%2Frepo");
    expect(useWorkflowsNav().library.value?.repositoryName).toBe("repo");
  });
});
