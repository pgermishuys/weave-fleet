import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { clearPendingPrompts, clearSentPrompts } from "@/composables/use-send-prompt";
import type { FileDiffItem } from "@/lib/api-types";
import { useSessionsStore } from "@/stores/sessions";

const { navigateMock, params, search, diffState, apiFetchMock } = vi.hoisted(() => ({
  navigateMock: vi.fn(),
  params: { value: { id: "session-1" } },
  search: { value: { instanceId: "instance-1", view: "files" as const } as {
    instanceId?: string;
    parentSessionId?: string;
    view?: "files";
  } },
  diffState: {
    diffs: { value: [] as FileDiffItem[] },
    available: { value: true },
    isLoading: { value: false },
    error: { value: undefined as string | undefined },
    isStale: { value: false },
    fetchDiffs: vi.fn(),
    markStale: vi.fn(),
  },
  apiFetchMock: vi.fn(),
}));

vi.mock("@tanstack/vue-router", () => ({
  createFileRoute: () => (config: unknown) => ({
    config,
    useNavigate: () => navigateMock,
    useParams: () => params,
    useSearch: () => search,
  }),
  useRouter: () => ({ navigate: navigateMock }),
}));

vi.mock("@/lib/api-client", () => ({
  apiFetch: apiFetchMock,
}));

vi.mock("@/composables/use-diffs", () => ({
  useDiffs: () => ({
    diffs: diffState.diffs,
    available: diffState.available,
    isLoading: diffState.isLoading,
    error: diffState.error,
    isStale: diffState.isStale,
    fetchDiffs: diffState.fetchDiffs,
    markStale: diffState.markStale,
  }),
}));

vi.mock("@/components/session/ActivityStream.vue", () => ({
  default: {
    name: "ActivityStreamStub",
    props: {
      sessionId: { type: String, required: true },
      instanceId: { type: String, required: false },
    },
    template: '<section data-testid="activity-stream">{{ sessionId }}:{{ instanceId }}</section>',
  },
}));

vi.mock("@/components/session/Composer.vue", () => ({
  default: {
    name: "ComposerStub",
    props: {
      sessionId: { type: String, required: true },
      instanceId: { type: String, required: false },
      disabled: { type: Boolean, required: true },
    },
    emits: ["promptSent"],
    methods: {
      focusPrompt() {},
    },
    template: '<button type="button" data-testid="composer" :disabled="disabled" @click="$emit(\'promptSent\')">{{ sessionId }}:{{ instanceId }}</button>',
  },
}));

vi.mock("@/components/session/FilesChangedView.vue", () => ({
  default: {
    name: "FilesChangedViewStub",
    emits: ["close", "select", "retry"],
    template: '<button type="button" data-testid="files-changed-view" @click="$emit(\'close\')">Files changed</button>',
  },
}));

vi.mock("@/components/session/DiffsTray.vue", () => ({
  default: {
    name: "DiffsTrayStub",
    props: {
      open: { type: Boolean, required: true },
    },
    emits: ["update:open", "select", "retry"],
    template: '<aside v-if="open" data-testid="diffs-tray">Diff tray</aside>',
  },
}));

vi.mock("@/components/session/SessionDetailHeader.vue", () => ({
  default: {
    name: "SessionDetailHeaderStub",
    template: '<header data-testid="session-detail-header">Header</header>',
  },
}));

describe("session route files view close", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    navigateMock.mockReset();
    diffState.fetchDiffs.mockReset();
    diffState.diffs.value = [];
    diffState.available.value = true;
    diffState.isLoading.value = false;
    diffState.error.value = undefined;
    diffState.isStale.value = false;
    params.value = { id: "session-1" };
    search.value = { instanceId: "instance-1", view: "files" };
    clearPendingPrompts("session-1");
    clearSentPrompts("session-1");
    apiFetchMock.mockResolvedValue(Response.json({
      id: "session-1",
      instanceId: "instance-1",
      title: "Session one",
      lifecycleStatus: "running",
      activityStatus: "idle",
      retentionStatus: "active",
    }));
  });

  it("renders_chat_components_with_session_props_and_keeps_prompt_flow_wired", async () => {
    search.value = { instanceId: "instance-1" };
    const { Route } = await import("@/routes/sessions.$id");
    const SessionDetailPage = (Route as unknown as { config: { component: unknown } }).config.component;

    const wrapper = mount(SessionDetailPage, {
      attachTo: document.body,
    });
    await flushPromises();

    const activityStream = wrapper.get('[data-testid="activity-stream"]');
    const composer = wrapper.get('[data-testid="composer"]');
    expect(activityStream.text()).toBe("session-1:instance-1");
    expect(composer.text()).toBe("session-1:instance-1");
    expect((composer.element as HTMLButtonElement).disabled).toBe(false);

    await composer.trigger("click");
    await flushPromises();

    const sessionsStore = useSessionsStore();
    expect(sessionsStore.sessions.find((item) => item.session.id === "session-1")).toMatchObject({
      activityStatus: "busy",
      lifecycleStatus: "running",
      sessionStatus: "active",
    });
  });

  it("restores_chat_mode_and_clears_files_view_search_when_files_view_closes", async () => {
    const { Route } = await import("@/routes/sessions.$id");
    const SessionDetailPage = (Route as unknown as { config: { component: unknown } }).config.component;

    const wrapper = mount(SessionDetailPage, {
      attachTo: document.body,
    });
    await flushPromises();

    expect(wrapper.find('[data-testid="files-changed-view"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="activity-stream"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="composer"]').exists()).toBe(false);

    await wrapper.get('[data-testid="files-changed-view"]').trigger("click");
    await flushPromises();

    expect(navigateMock).toHaveBeenCalledWith({
      to: "/sessions/$id",
      params: { id: "session-1" },
      search: {
        instanceId: "instance-1",
        parentSessionId: undefined,
        view: undefined,
      },
      replace: true,
    });
    expect(wrapper.find('[data-testid="files-changed-view"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="activity-stream"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="composer"]').exists()).toBe(true);
  });

  it("navigates_to_files_search_and_replaces_chat_when_files_changed_badge_is_clicked", async () => {
    search.value = { instanceId: "instance-1" };

    const { Route } = await import("@/routes/sessions.$id");
    const SessionDetailPage = (Route as unknown as { config: { component: unknown } }).config.component;
    const pinia = createPinia();
    setActivePinia(pinia);
    useSessionsStore(pinia).setSessions([{
      instanceId: "instance-1",
      workspaceId: "workspace-1",
      workspaceDirectory: "/workspace/project",
      workspaceDisplayName: "project",
      isolationStrategy: "existing",
      sessionStatus: "idle",
      session: {
        id: "session-1",
        title: "Session one",
        time: {
          created: 0,
          updated: 0,
        },
      },
      instanceStatus: "running",
      sourceDirectory: null,
      branch: null,
      activityStatus: "idle",
      lifecycleStatus: "running",
      retentionStatus: "active",
      archivedAt: null,
      typedInstanceStatus: "running",
      isHidden: false,
    }]);
    diffState.diffs.value = [{
      file: "src/changed.ts",
      status: "modified",
      additions: 2,
      deletions: 1,
      before: "export const value = 1;\n",
      after: "export const value = 2;\nexport const extra = true;\n",
    }];

    const wrapper = mount(SessionDetailPage, {
      attachTo: document.body,
    });
    await flushPromises();

    expect(wrapper.find('[data-testid="activity-stream"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="composer"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="files-changed-view"]').exists()).toBe(false);

    const exposed = wrapper.vm as unknown as { setViewMode: (viewMode: "files-changed") => Promise<void> };
    await exposed.setViewMode("files-changed");
    await flushPromises();

    expect(navigateMock).toHaveBeenCalledWith({
      to: "/sessions/$id",
      params: { id: "session-1" },
      search: {
        instanceId: "instance-1",
        parentSessionId: undefined,
        view: "files",
      },
      replace: true,
    });
    expect(wrapper.find('[data-testid="files-changed-view"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="activity-stream"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="composer"]').exists()).toBe(false);
  });

  it("opens_diffs_tray_over_chat_when_context_open_handler_runs", async () => {
    search.value = { instanceId: "instance-1" };
    const { useSessionDiffsContext } = await import("@/composables/use-session-diffs-context");
    const { Route } = await import("@/routes/sessions.$id");
    const SessionDetailPage = (Route as unknown as { config: { component: unknown } }).config.component;

    const wrapper = mount(SessionDetailPage, {
      attachTo: document.body,
    });
    await flushPromises();

    useSessionDiffsContext().value?.openDiffsTray?.();
    await flushPromises();

    expect(wrapper.find('[data-testid="diffs-tray"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="activity-stream"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="composer"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="files-changed-view"]').exists()).toBe(false);
  });

  it("refreshes_stale_diffs_when_tray_opens", async () => {
    search.value = { instanceId: "instance-1" };
    diffState.isStale.value = true;
    const { Route } = await import("@/routes/sessions.$id");
    const { useSessionDiffsContext } = await import("@/composables/use-session-diffs-context");
    const SessionDetailPage = (Route as unknown as { config: { component: unknown } }).config.component;

    mount(SessionDetailPage, {
      attachTo: document.body,
    });
    await flushPromises();
    diffState.fetchDiffs.mockClear();

    useSessionDiffsContext().value?.openDiffsTray?.();
    await flushPromises();

    expect(diffState.fetchDiffs).toHaveBeenCalledTimes(1);
  });
});
