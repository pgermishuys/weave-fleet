import { flushPromises, mount } from "@vue/test-utils";
import { nextTick } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import GitHubWorkItemDetailPage from "@/components/pages/GitHubWorkItemDetailPage.vue";
import { useSidebarStore } from "@/stores/sidebar";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";

const { apiFetchMock } = vi.hoisted(() => ({
  apiFetchMock: vi.fn(),
}));

vi.mock("@/lib/api-client", () => ({
  apiFetch: apiFetchMock,
}));

function createJsonResponse<T>(body: T, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("GitHubWorkItemDetailPage", () => {
  beforeEach(() => {
    apiFetchMock.mockReset();
    apiFetchMock.mockImplementation(async (url: string) => {
      if (url.endsWith("/comments")) {
        return createJsonResponse([]);
      }

      return createJsonResponse({
        id: 42,
        number: 42,
        title: "Investigate missing GitHub context",
        body: "Body",
        html_url: "https://github.com/acme/weave/issues/42",
        state: "open",
        labels: [],
        user: {
          login: "octocat",
          avatar_url: "https://avatars.githubusercontent.com/u/1",
        },
        comments: 0,
        created_at: "2026-04-01T00:00:00Z",
        updated_at: "2026-04-02T00:00:00Z",
      });
    });
  });

  it("waits for the sessions panel switch before opening the dialog", async () => {
    const sidebarStore = useSidebarStore();
    const workspaceUiStore = useWorkspaceUiStore();
    const openNewSessionDialogSpy = vi.spyOn(workspaceUiStore, "openNewSessionDialog");

    const wrapper = mount(GitHubWorkItemDetailPage, {
      props: {
        owner: "acme",
        repo: "weave",
        number: "42",
        kind: "issue",
      },
      global: {
        stubs: {
          Button: {
            template: '<button type="button" @click="$emit(\'click\')"><slot /></button>',
          },
        },
      },
    });

    await flushPromises();

    const createSessionButton = wrapper.get("button");
    createSessionButton.element.click();

    expect(sidebarStore.panelCollapsed).toBe(false);
    expect(sidebarStore.activeRail).toBe("sessions");
    expect(openNewSessionDialogSpy).not.toHaveBeenCalled();

    await nextTick();

    expect(openNewSessionDialogSpy).toHaveBeenCalledWith(
      null,
      expect.objectContaining({
        kind: "github",
        sourceType: "github-issue",
        owner: "acme",
        repo: "weave",
        number: 42,
      }),
    );
  });
});
