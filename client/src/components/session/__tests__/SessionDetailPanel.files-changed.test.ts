import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { computed, readonly, ref, shallowRef } from "vue";
import type { Ref, ShallowRef } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { FileDiffItem, SessionListItem } from "@/lib/api-types";
import { SessionDetailContextKey, type SessionDetailContext } from "@/composables/use-session-detail-context";
import { SessionDiffsContextKey } from "@/composables/use-session-diffs-context";
import { useSessionsStore } from "@/stores/sessions";
import { useSidebarStore } from "@/stores/sidebar";

const routerNavigateMock = vi.fn();

const { apiFetchMock, diffState } = vi.hoisted(() => ({
  apiFetchMock: vi.fn(),
  diffState: {
    diffs: null as unknown as Ref<FileDiffItem[]>,
    available: null as unknown as ShallowRef<boolean>,
    isLoading: null as unknown as ShallowRef<boolean>,
    error: null as unknown as ShallowRef<string | undefined>,
    fetchDiffs: vi.fn(),
  },
}));

vi.mock("@tanstack/vue-router", () => ({
  useRouter: () => ({ navigate: routerNavigateMock }),
}));

vi.mock("@/lib/api-client", () => ({
  apiFetch: apiFetchMock,
}));

vi.mock("@/composables/use-session-todos", async () => {
  const { computed: vueComputed } = await import("vue");

  return {
    useSessionTodos: () => ({ todos: vueComputed(() => []) }),
  };
});

const FilesChangedStub = {
  name: "FilesChangedStub",
  props: {
    files: {
      type: Array,
      default: () => [],
    },
    isLoading: {
      type: Boolean,
      default: false,
    },
    error: {
      type: String,
      default: null,
    },
    unavailable: {
      type: Boolean,
      default: false,
    },
  },
  emits: ["click"],
  template: `
    <button
      type="button"
      class="files-changed__badge"
      aria-expanded="false"
      :disabled="isLoading"
      @click="$emit('click', { open: true, fileCount: files.length, additions: 0, deletions: 0 })"
    >
      {{ files.length }} files changed
    </button>
  `,
};

import SessionDetailPanel from "@/components/session/SessionDetailPanel.vue";
import SessionsV2RightPanel from "@/components/sessions/SessionsV2RightPanel.vue";

function createSession(): SessionListItem {
  return {
    instanceId: "instance-1",
    workspaceId: "workspace-1",
    workspaceDirectory: "/workspace/project",
    workspaceDisplayName: "project",
    isolationStrategy: "existing",
    sessionStatus: "active",
    session: {
      id: "session-1",
      title: "Files changed session",
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
  };
}

function createSessionDetailContext(): SessionDetailContext {
  const falseRef = () => readonly(shallowRef(false));
  const emptyErrorRef = () => readonly(shallowRef<string | undefined>(undefined));

  return {
    apiBasePath: "/api/sessions",
    sessionRoutePath: "/sessions/$id",
    supportsFork: false,
    supportsArchive: false,
    actionsLayout: "toolbar",
    patchSession: vi.fn(),
    abort: {
      abortSession: vi.fn(async () => undefined),
      isAborting: falseRef(),
      error: emptyErrorRef(),
    },
    archive: {
      archiveSession: vi.fn(async () => undefined),
      isArchiving: falseRef(),
      error: emptyErrorRef(),
    },
    delete: {
      deleteSession: vi.fn(async () => undefined),
      isDeleting: falseRef(),
      error: emptyErrorRef(),
    },
    rename: {
      renameSession: vi.fn(async () => undefined),
      isLoading: falseRef(),
      error: emptyErrorRef(),
    },
    resume: {
      resumeSession: vi.fn(async () => ({
        instanceId: "instance-1",
        session: createSession().session,
      })),
      isResuming: computed(() => false),
      resumingSessionId: readonly(shallowRef<string | null>(null)),
      error: emptyErrorRef(),
    },
    terminate: {
      terminateSession: vi.fn(async () => undefined),
      isTerminating: falseRef(),
      error: emptyErrorRef(),
    },
  };
}

function mountPanel(setViewMode = vi.fn()) {
  return mount(SessionDetailPanel, {
    props: {
      session: createSession(),
      setViewMode,
    },
    global: {
      stubs: {
        FilesChanged: FilesChangedStub,
      },
      provide: {
        [SessionDetailContextKey as symbol]: createSessionDetailContext(),
        [SessionDiffsContextKey as symbol]: {
          diffState: {
            diffs: readonly(diffState.diffs),
            available: readonly(diffState.available),
            isLoading: readonly(diffState.isLoading),
            error: readonly(diffState.error),
            fetchDiffs: diffState.fetchDiffs,
          },
        },
      },
    },
  });
}

describe("SessionDetailPanel files changed integration", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    diffState.diffs = ref<FileDiffItem[]>([]);
    diffState.available = shallowRef(false);
    diffState.isLoading = shallowRef(false);
    diffState.error = shallowRef<string | undefined>(undefined);
    routerNavigateMock.mockReset();
    diffState.fetchDiffs.mockReset();
    diffState.diffs.value = [
      {
        file: "src/summary-only.ts",
        status: "modified",
        additions: 2,
        deletions: 1,
      } as FileDiffItem,
      {
        file: "src/with-content.ts",
        status: "modified",
        additions: 2,
        deletions: 1,
        before: "export const value = 1;\n",
        after: "export const value = 2;\nexport const extra = true;\n",
      },
    ];
    diffState.available.value = true;
    diffState.isLoading.value = false;
    diffState.error.value = undefined;
    apiFetchMock.mockImplementation(async (url: string) => {
      if (url.includes("smart-links")) {
        return Response.json([]);
      }

      return Response.json({
        id: "session-1",
        instanceId: "instance-1",
        title: "Files changed session",
        lifecycleStatus: "running",
        activityStatus: "idle",
        retentionStatus: "active",
      });
    });
  });

  it("does_not_switch_to_files_changed_view_from_badge_without_tray_handler", async () => {
    const setViewMode = vi.fn();
    const wrapper = mountPanel(setViewMode);
    await flushPromises();

    expect(wrapper.get(".files-changed__badge").attributes("aria-expanded")).toBe("false");

    await wrapper.get(".files-changed__badge").trigger("click");

    expect(setViewMode).not.toHaveBeenCalled();
    expect(wrapper.get(".files-changed__badge").attributes("aria-expanded")).toBe("false");
  });

  it("opens_diffs_tray_from_badge_when_handler_is_available", async () => {
    const setViewMode = vi.fn();
    const openDiffsTray = vi.fn();
    const wrapper = mount(SessionDetailPanel, {
      props: {
        session: createSession(),
        setViewMode,
        openDiffsTray,
      },
      global: {
        stubs: {
          FilesChanged: FilesChangedStub,
        },
        provide: {
          [SessionDetailContextKey as symbol]: createSessionDetailContext(),
          [SessionDiffsContextKey as symbol]: {
            diffState: {
              diffs: readonly(diffState.diffs),
              available: readonly(diffState.available),
              isLoading: readonly(diffState.isLoading),
              error: readonly(diffState.error),
              fetchDiffs: diffState.fetchDiffs,
            },
          },
        },
      },
    });
    await flushPromises();

    await wrapper.get(".files-changed__badge").trigger("click");

    expect(openDiffsTray).toHaveBeenCalledTimes(1);
    expect(setViewMode).not.toHaveBeenCalled();
  });

  it("right_panel_badge_click_opens_diffs_tray", async () => {
    const openDiffsTray = vi.fn();
    const pinia = createPinia();
    setActivePinia(pinia);
    useSessionsStore(pinia).setSessions([createSession()]);
    useSessionsStore(pinia).setActiveSessionId("session-1");
    useSidebarStore(pinia).setRightPanelCollapsed(false);

    const wrapper = mount(SessionsV2RightPanel, {
      global: {
        plugins: [pinia],
        stubs: {
          FilesChanged: FilesChangedStub,
          RightPanelTabs: {
            name: "RightPanelTabsStub",
            emits: ["collapse"],
            template: '<div data-testid="right-panel-tabs" />',
          },
          CollapsedRightRail: {
            name: "CollapsedRightRailStub",
            emits: ["expand"],
            template: '<button type="button" data-testid="collapsed-right-rail" />',
          },
        },
        provide: {
          [SessionDiffsContextKey as symbol]: {
            diffState: {
              diffs: readonly(diffState.diffs),
              available: readonly(diffState.available),
              isLoading: readonly(diffState.isLoading),
              error: readonly(diffState.error),
              fetchDiffs: diffState.fetchDiffs,
            },
            openDiffsTray,
          },
        },
      },
    });
    await flushPromises();

    await wrapper.get(".files-changed__badge").trigger("click");

    expect(openDiffsTray).toHaveBeenCalledTimes(1);
    expect(routerNavigateMock).not.toHaveBeenCalled();
  });
});
