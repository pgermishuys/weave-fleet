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

function createCapabilities(overrides: Partial<NonNullable<SessionListItem["capabilities"]>> = {}): NonNullable<SessionListItem["capabilities"]> {
  return {
    canPrompt: true,
    canStop: true,
    canResume: false,
    canRestart: false,
    canAbort: false,
    canArchive: false,
    canUnarchive: false,
    canFork: false,
    canDelete: true,
    promptDisabledReason: null,
    stopDisabledReason: null,
    resumeDisabledReason: null,
    restartDisabledReason: null,
    abortDisabledReason: null,
    archiveDisabledReason: null,
    unarchiveDisabledReason: null,
    forkDisabledReason: null,
    deleteDisabledReason: null,
    ...overrides,
  };
}

function createSession(overrides: Partial<SessionListItem> = {}): SessionListItem {
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
    capabilities: createCapabilities(),
    ...overrides,
  };
}

function createSessionDetailContext(overrides: Partial<Pick<SessionDetailContext, "supportsArchive" | "supportsFork">> = {}): SessionDetailContext {
  const falseRef = () => readonly(shallowRef(false));
  const emptyErrorRef = () => readonly(shallowRef<string | undefined>(undefined));

  return {
    apiBasePath: "/api/sessions",
    sessionRoutePath: "/sessions/$id",
    supportsFork: false,
    supportsArchive: false,
    ...overrides,
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

interface MountPanelOptions {
  openDiffsTray?: () => void;
  session?: SessionListItem;
  context?: Partial<Pick<SessionDetailContext, "supportsArchive" | "supportsFork">>;
}

function mountPanel(options: MountPanelOptions = {}) {
  return mount(SessionDetailPanel, {
    props: {
      session: options.session ?? createSession(),
      openDiffsTray: options.openDiffsTray ?? vi.fn(),
    },
    global: {
      stubs: {
        FilesChanged: FilesChangedStub,
      },
      provide: {
        [SessionDetailContextKey as symbol]: createSessionDetailContext(options.context),
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
        capabilities: createCapabilities(),
      });
    });
  });

  it("opens_diffs_tray_from_badge_when_handler_is_available", async () => {
    const openDiffsTray = vi.fn();
    const wrapper = mountPanel({ openDiffsTray });
    await flushPromises();

    await wrapper.get(".files-changed__badge").trigger("click");

    expect(openDiffsTray).toHaveBeenCalledTimes(1);
  });

  it("shows_toolbar_actions_enabled_by_session_capabilities", async () => {
    const wrapper = mountPanel({
      context: { supportsArchive: true },
      session: createSession({
        capabilities: createCapabilities({
          canAbort: true,
          canResume: true,
          canStop: true,
          canArchive: true,
          canFork: true,
          canDelete: true,
        }),
      }),
    });
    await flushPromises();

    expect(wrapper.find("[data-testid='abort-button']").exists()).toBe(true);
    expect(wrapper.find("[data-testid='session-resume-button']").exists()).toBe(true);
    expect(wrapper.find("[data-testid='session-stop-button']").exists()).toBe(true);
    expect(wrapper.find("[data-testid='session-archived-fork-button']").exists()).toBe(true);
    expect(wrapper.find("[data-testid='session-archive-banner-button']").exists()).toBe(true);
    expect(wrapper.find("[data-testid='session-delete-button']").exists()).toBe(true);
  });

  it("hides_toolbar_actions_disabled_by_session_capabilities", async () => {
    const wrapper = mountPanel({
      context: { supportsArchive: true },
      session: createSession({
        capabilities: createCapabilities({
          canAbort: false,
          canResume: false,
          canStop: false,
          canArchive: false,
          canFork: false,
          canDelete: false,
        }),
      }),
    });
    await flushPromises();

    expect(wrapper.find("[data-testid='abort-button']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='session-resume-button']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='session-stop-button']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='session-archived-fork-button']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='session-archive-banner-button']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='session-delete-button']").exists()).toBe(false);
  });

  it("hides_resume_and_stop_for_stopped_automatic_session_capabilities", async () => {
    const wrapper = mountPanel({
      session: createSession({
        sessionStatus: "stopped",
        instanceStatus: "dead",
        activityStatus: "idle",
        lifecycleStatus: "stopped",
        typedInstanceStatus: "stopped",
        capabilities: createCapabilities({
          canPrompt: true,
          canResume: false,
          canStop: false,
        }),
      }),
    });
    await flushPromises();

    expect(wrapper.find("[data-testid='session-resume-button']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='session-stop-button']").exists()).toBe(false);
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
