import { config, flushPromises, mount } from "@vue/test-utils";
import { ref, type DefineComponent } from "vue";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearPendingPrompts, clearSentPrompts } from "@/composables/use-send-prompt";
import { MACHINE_TARGET, type MachineTarget } from "@/lib/machine-target";
import { useSessionsStore } from "@/stores/sessions";
import { useArchiveQueueStore } from "@/stores/archive-queue";
import type { SessionListItem } from "@/api/client";

const h = vi.hoisted(() => ({
  navigateMock: vi.fn(),
  params: { value: { id: "session-1" } },
  search: { value: {} as { instanceId?: string; parentSessionId?: string; view?: "files" } },
  apiFetchMock: vi.fn(),
  abortSession: vi.fn(),
  deleteSession: vi.fn(),
  renameSession: vi.fn(),
}));

vi.mock("@tanstack/vue-router", () => ({
  createFileRoute: () => (config: unknown) => ({
    config,
    useNavigate: () => h.navigateMock,
    useParams: () => h.params,
    useSearch: () => h.search,
  }),
  useRouter: () => ({ navigate: h.navigateMock }),
}));

vi.mock("@/lib/api-client", () => ({
  apiFetch: h.apiFetchMock,
  apiFetchOn: (machine: unknown, ...args: unknown[]) => h.apiFetchMock(machine, ...args),
}));

vi.mock("@/composables/use-session-actions", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/composables/use-session-actions")>()),
  useAbortSession: () => ({ abortSession: h.abortSession, isAborting: ref(false), error: ref(null) }),
  useDeleteSession: () => ({ deleteSession: h.deleteSession, isDeleting: ref(false), error: ref(null) }),
  useRenameSession: () => ({ renameSession: h.renameSession, isLoading: ref(false), error: ref(null) }),
}));

vi.mock("@/composables/use-diffs", () => ({
  useDiffs: () => ({
    diffs: ref([]), available: ref(true), isLoading: ref(false), error: ref(undefined), isStale: ref(false),
    fetchDiffs: vi.fn(), markStale: vi.fn(),
  }),
}));
vi.mock("@/composables/use-session-terminals", () => ({
  useSessionTerminals: vi.fn(), openNewTerminal: vi.fn(), closeTerminalTab: vi.fn(),
}));
vi.mock("@/composables/use-session-recap", () => ({
  useSessionRecap: () => ({ value: null }),
  SESSION_RECAP_PREFERENCE_KEY: "SessionRecap",
}));
vi.mock("@/components/session/ActivityStream.vue", () => ({ default: { name: "ActivityStreamStub", template: "<section />" } }));
vi.mock("@/components/session/SideConversationPanel.vue", () => ({ default: { name: "SideStub", template: "<div />" } }));
vi.mock("@/components/session/Composer.vue", () => ({
  default: { name: "ComposerStub", methods: { focusPrompt() {} }, template: '<div data-testid="composer" />' },
}));
vi.mock("@/components/session/FilesChangedView.vue", () => ({ default: { name: "FilesStub", template: "<div />" } }));
vi.mock("@/components/session/DiffsTray.vue", () => ({ default: { name: "TrayStub", template: "<div />" } }));
vi.mock("@/components/session/ForkSessionDialog.vue", () => ({
  default: {
    name: "ForkStub",
    props: { open: Boolean },
    template: '<div data-testid="fork-dialog" :data-open="open" />',
  },
}));
vi.mock("@/components/sessions/ConfirmDeleteSessionDialog.vue", () => ({
  default: {
    name: "ConfirmDeleteStub",
    props: { open: Boolean },
    emits: ["confirm", "update:open"],
    template: '<button data-testid="confirm-delete" :data-open="open" @click="$emit(\'confirm\')" />',
  },
}));
vi.mock("@/components/session/SessionDetailHeader.vue", () => ({
  default: {
    name: "HeaderStub",
    props: {
      title: { type: String, required: false },
      retentionStatus: { type: String, required: false },
    },
    emits: ["rename", "restore"],
    template: `<header data-testid="header" :data-title="title" :data-retention="retentionStatus">
      <button data-testid="header-rename" @click="$emit('rename', 'New name')" />
      <button data-testid="header-restore" @click="$emit('restore')" />
      <slot name="actions" /></header>`,
  },
}));
vi.mock("@/components/session/SessionActionToolbar.vue", () => ({
  default: {
    name: "ToolbarStub",
    props: { canAbort: Boolean, canArchive: Boolean, canRestore: Boolean, canFork: Boolean, canDelete: Boolean },
    emits: ["abort", "fork", "delete", "archive", "restore"],
    template: `<div data-testid="toolbar" :data-can-abort="canAbort" :data-can-archive="canArchive"
      :data-can-restore="canRestore" :data-can-fork="canFork" :data-can-delete="canDelete">
      <button data-testid="act-abort" @click="$emit('abort')" />
      <button data-testid="act-fork" @click="$emit('fork')" />
      <button data-testid="act-delete" @click="$emit('delete')" />
      <button data-testid="act-archive" @click="$emit('archive')" />
      <button data-testid="act-restore" @click="$emit('restore')" /></div>`,
  },
}));

function listItem(patch: Partial<SessionListItem> & { id?: string } = {}): SessionListItem {
  const { id = "session-1", ...rest } = patch;
  return {
    harnessType: "opencode",
    instanceId: "instance-list",
    workspaceId: "workspace-list",
    workspaceDirectory: "/workspace/list",
    workspaceDisplayName: "List project",
    isolationStrategy: "worktree",
    sessionStatus: "idle",
    session: { id, title: "List title", time: { created: 111, updated: 222 }, tags: ["from-list"] },
    instanceStatus: "running",
    sourceDirectory: "/src/list",
    branch: "list-branch",
    activityStatus: "idle",
    lifecycleStatus: "running",
    retentionStatus: "active",
    archivedAt: null,
    typedInstanceStatus: "running",
    isHidden: false,
    tags: ["from-list"],
    totalTokens: 10,
    totalCost: 0.5,
    projectId: "p-list",
    projectName: "List project",
    ...rest,
  } as SessionListItem;
}

const capabilities = {
  canPrompt: true, canRestart: true, canAbort: true, canArchive: true, canUnarchive: false, canFork: true,
  canDelete: true, promptDisabledReason: null, restartDisabledReason: null, abortDisabledReason: null,
  archiveDisabledReason: null, unarchiveDisabledReason: null, forkDisabledReason: null, deleteDisabledReason: null,
};

// A mounted route listens for session upserts for as long as it lives, so each test unmounts what it mounted.
const mounted: Array<{ unmount: () => void }> = [];
afterEach(() => {
  while (mounted.length) mounted.pop()?.unmount();
});

async function mountRoute(machine?: MachineTarget) {
  const { Route } = await import("@/routes/sessions.$id");
  const Page = (Route as unknown as { config: { component: DefineComponent } }).config.component;
  const wrapper = mount(Page, {
    attachTo: document.body,
    global: machine ? { provide: { [MACHINE_TARGET as symbol]: () => machine } } : {},
  });
  mounted.push(wrapper);
  await flushPromises();
  return wrapper;
}

/** A fresh store for a second mount in the same test (mount installs `config.global.plugins`' pinia). */
function freshPinia(): void {
  const pinia = createPinia();
  setActivePinia(pinia);
  config.global.plugins = [pinia];
}

function respond(payload: unknown, status = 200) {
  h.apiFetchMock.mockResolvedValue(
    status === 200 ? Response.json(payload) : new Response(null, { status }),
  );
}

function storeItem() {
  return useSessionsStore().sessions.find((i) => i.session.id === "session-1");
}

describe("session route: detail fetch", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    h.params.value = { id: "session-1" };
    h.search.value = { instanceId: "instance-1" };
    clearPendingPrompts("session-1");
    clearSentPrompts("session-1");
    h.abortSession.mockResolvedValue(undefined);
    h.deleteSession.mockResolvedValue(undefined);
    h.renameSession.mockResolvedValue(undefined);
  });

  it("fetches_the_session_by_encoded_id_and_marks_it_active", async () => {
    h.params.value = { id: "a b/c" };
    respond({ id: "a b/c" });
    await mountRoute();
    expect(h.apiFetchMock.mock.calls[0]?.[1]).toBe("/api/sessions/a%20b%2Fc");
    expect(useSessionsStore().activeSessionId).toBe("a b/c");
  });

  it("builds_a_store_item_from_a_full_camelcase_payload", async () => {
    respond({
      id: "session-1", instanceId: "instance-api", workspaceId: "ws-api", workspaceDirectory: "/workspace/api",
      workspaceDisplayName: "Api project", sourceDirectory: "/src/api", isolationStrategy: "worktree", branch: "feat/x",
      title: "Api title", createdAt: "2026-01-02T03:04:05.000Z", projectId: "p-api", projectName: "Api project",
      lifecycleStatus: "running", activityStatus: "busy", retentionStatus: "active", totalTokens: 1200, totalCost: 1.25,
      capabilities, origin: { sourceType: "issue", title: "Issue", resourceUrl: "https://x.test/1", resourceId: "1", providerId: "gh" },
      harnessType: "opencode", tags: ["a", "b"], parentSessionId: "parent-9", forkedFromSessionId: "fork-9",
      spawnedBySessionId: "spawn-9", spawnKind: "delegation",
    });
    await mountRoute();
    expect(storeItem()).toMatchObject({
      instanceId: "instance-api", workspaceId: "ws-api", workspaceDirectory: "/workspace/api",
      workspaceDisplayName: "Api project", isolationStrategy: "worktree", sessionStatus: "active",
      session: { id: "session-1", title: "Api title", time: { created: Date.parse("2026-01-02T03:04:05.000Z") }, tags: ["a", "b"] },
      parentSessionId: "parent-9", forkedFromSessionId: "fork-9", spawnedBySessionId: "spawn-9", spawnKind: "delegation",
      sourceDirectory: "/src/api", branch: "feat/x", activityStatus: "busy", lifecycleStatus: "running",
      retentionStatus: "active", totalTokens: 1200, totalCost: 1.25, projectId: "p-api", projectName: "Api project",
      harnessType: "opencode", tags: ["a", "b"],
      origin: { sourceType: "issue", title: "Issue", resourceUrl: "https://x.test/1", resourceId: "1", providerId: "gh" },
    });
    expect(storeItem()?.session.time.updated).toBe(storeItem()?.session.time.created);
    expect(storeItem()?.capabilities).toEqual(capabilities);
  });

  it("accepts_a_PascalCase_payload", async () => {
    respond({
      Id: "session-1", InstanceId: "inst-pascal", WorkspaceDirectory: "/workspace/pascal", Title: "Pascal title",
      LifecycleStatus: "completed", ActivityStatus: "idle", RetentionStatus: "archived", TotalTokens: 7,
      Origin: { SourceType: "pr", Title: "PR", ResourceUrl: "u", ResourceId: "r", ProviderId: "p" }, Tags: ["t"],
    });
    await mountRoute();
    expect(storeItem()).toMatchObject({
      instanceId: "inst-pascal", workspaceDirectory: "/workspace/pascal", sessionStatus: "completed",
      lifecycleStatus: "completed", retentionStatus: "archived", totalTokens: 7, tags: ["t"],
      session: { title: "Pascal title" },
      origin: { sourceType: "pr", title: "PR", resourceUrl: "u", resourceId: "r", providerId: "p" },
    });
  });

  it("falls_back_to_defaults_for_an_empty_object_and_ignores_a_non_object_body", async () => {
    respond({});
    await mountRoute();
    expect(storeItem()).toMatchObject({
      instanceId: "instance-1", workspaceId: "", workspaceDirectory: "", workspaceDisplayName: null,
      isolationStrategy: "existing", sessionStatus: "idle", session: { id: "session-1", title: "Untitled session", tags: [] },
      instanceStatus: "running", parentSessionId: null, activityStatus: "idle", lifecycleStatus: "running",
      retentionStatus: "active", typedInstanceStatus: "running", isHidden: false, harnessType: "", origin: null,
      projectId: null, projectName: null, tags: [],
    });
    expect(storeItem()?.session.time.created).toBeGreaterThan(1_000_000_000_000);

    freshPinia();
    respond("nonsense");
    await mountRoute();
    expect(storeItem()?.session.title).toBe("Untitled session");
  });

  it("keeps_values_the_list_already_has_when_the_payload_leaves_them_null_or_missing", async () => {
    useSessionsStore().setSessions([listItem()]);
    respond({ id: "session-1", title: null, workspaceDirectory: null, tags: null, totalTokens: null, origin: null, branch: null });
    await mountRoute();
    expect(storeItem()).toMatchObject({
      instanceId: "instance-1", workspaceDirectory: "/workspace/list", branch: "list-branch", totalTokens: 10,
      totalCost: 0.5, projectId: "p-list", harnessType: "opencode", tags: ["from-list"], isolationStrategy: "worktree",
      session: { title: "List title", time: { created: 111, updated: 222 }, tags: ["from-list"] },
    });
  });

  it("uses_the_payload_instance_over_the_search_instance", async () => {
    respond({ id: "session-1", instanceId: "instance-api" });
    await mountRoute();
    expect(storeItem()?.instanceId).toBe("instance-api");
  });

  it.each([
    ["active", "busy", "running", "busy", "active"],
    ["idle", "idle", "running", "idle", "idle"],
    ["delegating", "delegating", "running", "delegating", "active"],
    ["running", "retry", "running", "retry", "active"],
    ["waiting_input", "waiting_input", "running", "waiting_input", "waiting_input"],
    ["complete", "idle", "completed", "idle", "completed"],
    ["disconnected", "busy", "disconnected", "busy", "disconnected"],
    ["stopped", "idle", "stopped", "idle", "stopped"],
    ["error", "idle", "error", "idle", "error"],
    ["bogus", "bogus", "running", "idle", "idle"],
  ])("normalises_lifecycle_%s_and_activity_%s", async (lifecycle, activity, expLife, expActivity, expSession) => {
    respond({ id: "session-1", lifecycleStatus: lifecycle, activityStatus: activity });
    await mountRoute();
    expect(storeItem()).toMatchObject({ lifecycleStatus: expLife, activityStatus: expActivity, sessionStatus: expSession });
  });

  it("falls_back_to_the_legacy_status_field_when_lifecycle_is_missing", async () => {
    respond({ id: "session-1", status: "complete" });
    await mountRoute();
    expect(storeItem()?.lifecycleStatus).toBe("completed");
  });

  it("treats_any_retention_other_than_archived_as_active", async () => {
    respond({ id: "session-1", retentionStatus: "weird" });
    await mountRoute();
    expect(storeItem()?.retentionStatus).toBe("active");
  });

  it("keeps_retry_details_only_while_the_session_is_retrying", async () => {
    respond({
      id: "session-1", activityStatus: "retry", retryAttempt: 2, retryMaxAttempts: 5, retryMessage: "rate limited",
      retryNext: "2026-01-01T00:00:00Z",
    });
    await mountRoute();
    expect(storeItem()).toMatchObject({
      retryAttempt: 2, retryMaxAttempts: 5, retryMessage: "rate limited", retryNext: "2026-01-01T00:00:00Z",
    });

    freshPinia();
    respond({ id: "session-1", activityStatus: "busy", retryAttempt: 2, retryMessage: "x" });
    await mountRoute();
    expect(storeItem()).not.toHaveProperty("retryAttempt");
  });

  it("drops_wrongly_typed_fields", async () => {
    respond({ id: "session-1", title: 42, totalTokens: "many", tags: "nope", origin: "x" });
    await mountRoute();
    expect(storeItem()).toMatchObject({ session: { title: "Untitled session", tags: [] }, tags: [] });
    expect(storeItem()?.totalTokens).toBeUndefined();
  });

  it("shows_session_not_found_on_404_and_leaves_the_store_alone", async () => {
    respond(null, 404);
    const wrapper = await mountRoute();
    expect(wrapper.find('[data-testid="session-not-found"]').exists()).toBe(true);
    expect(storeItem()).toBeUndefined();
    await wrapper.get('[data-testid="session-not-found"] button').trigger("click");
    expect(h.navigateMock).toHaveBeenCalledWith({ to: "/" });
  });

  it("neither_shows_not_found_nor_stores_anything_on_other_failures", async () => {
    respond(null, 500);
    const wrapper = await mountRoute();
    expect(wrapper.find('[data-testid="session-not-found"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="header"]').exists()).toBe(true);
    expect(storeItem()).toBeUndefined();
  });

  it("swallows_a_network_error_and_an_abort", async () => {
    h.apiFetchMock.mockRejectedValue(new TypeError("offline"));
    const wrapper = await mountRoute();
    expect(wrapper.find('[data-testid="session-not-found"]').exists()).toBe(false);
    h.apiFetchMock.mockRejectedValue(new DOMException("aborted", "AbortError"));
    await mountRoute();
    expect(storeItem()).toBeUndefined();
  });

  it("fetches_the_new_id_when_the_route_is_opened_on_another_session", async () => {
    h.params.value = { id: "session-2" };
    respond(null, 404);
    const wrapper = await mountRoute();
    expect(wrapper.find('[data-testid="session-not-found"]').exists()).toBe(true);
    expect(h.apiFetchMock.mock.calls.at(-1)?.[1]).toBe("/api/sessions/session-2");
  });

  it("files_the_item_in_the_live_list_for_the_live_machine", async () => {
    respond({ id: "session-1", title: "Live" });
    await mountRoute();
    expect(storeItem()).toBeDefined();
    expect(useSessionsStore().elsewhere.size).toBe(0);
  });

  it("files_the_item_under_another_machine_and_asks_that_machine", async () => {
    const connection = { id: "m-2", name: "Other", url: "https://other.test", token: "t" };
    const machine = { key: "m-2", connection, isLive: false, api: {} } as unknown as MachineTarget;
    respond({ id: "session-1", title: "Away" });
    await mountRoute(machine);
    expect(h.apiFetchMock.mock.calls[0]?.[0]).toBe(connection);
    expect(storeItem()).toBeUndefined();
    expect(useSessionsStore().elsewhere.get("session-1")).toMatchObject({
      machineKey: "m-2", item: { session: { title: "Away" } },
    });
  });
});

describe("session route: actions", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    h.params.value = { id: "session-1" };
    h.search.value = { instanceId: "instance-1" };
    clearPendingPrompts("session-1");
    clearSentPrompts("session-1");
    h.abortSession.mockResolvedValue(undefined);
    h.deleteSession.mockResolvedValue(undefined);
    h.renameSession.mockResolvedValue(undefined);
    respond({ id: "session-1", title: "Original", lifecycleStatus: "running", activityStatus: "busy", capabilities });
  });

  it("abort_calls_abortSession_with_the_id", async () => {
    const wrapper = await mountRoute();
    await wrapper.get('[data-testid="act-abort"]').trigger("click");
    expect(h.abortSession).toHaveBeenCalledWith("session-1");
  });

  it("abort_is_skipped_when_capabilities_forbid_it", async () => {
    respond({ id: "session-1", capabilities: { ...capabilities, canAbort: false } });
    const wrapper = await mountRoute();
    await wrapper.get('[data-testid="act-abort"]').trigger("click");
    expect(h.abortSession).not.toHaveBeenCalled();
  });

  it("abort_failure_is_swallowed", async () => {
    h.abortSession.mockRejectedValue(new Error("nope"));
    const wrapper = await mountRoute();
    await wrapper.get('[data-testid="act-abort"]').trigger("click");
    await flushPromises();
    expect(h.abortSession).toHaveBeenCalledTimes(1);
  });

  it("fork_opens_the_fork_dialog_when_allowed", async () => {
    const wrapper = await mountRoute();
    expect(wrapper.get('[data-testid="fork-dialog"]').attributes("data-open")).toBe("false");
    await wrapper.get('[data-testid="act-fork"]').trigger("click");
    expect(wrapper.get('[data-testid="fork-dialog"]').attributes("data-open")).toBe("true");
  });

  it("fork_is_not_offered_when_the_harness_cannot_fork", async () => {
    respond({ id: "session-1", capabilities: { ...capabilities, canFork: false } });
    const wrapper = await mountRoute();
    expect(wrapper.find('[data-testid="fork-dialog"]').exists()).toBe(false);
    await wrapper.get('[data-testid="act-fork"]').trigger("click");
    expect(wrapper.find('[data-testid="fork-dialog"]').exists()).toBe(false);
  });

  it("delete_opens_the_confirm_dialog_and_confirming_deletes_then_goes_home", async () => {
    const wrapper = await mountRoute();
    expect(h.deleteSession).not.toHaveBeenCalled();
    await wrapper.get('[data-testid="act-delete"]').trigger("click");
    expect(wrapper.get('[data-testid="confirm-delete"]').attributes("data-open")).toBe("true");

    await wrapper.get('[data-testid="confirm-delete"]').trigger("click");
    await flushPromises();
    expect(h.deleteSession).toHaveBeenCalledWith("session-1", "instance-1");
    expect(h.navigateMock).toHaveBeenCalledWith({ to: "/" });
    expect(wrapper.get('[data-testid="confirm-delete"]').attributes("data-open")).toBe("false");
  });

  it("delete_failure_keeps_the_dialog_open_and_does_not_navigate", async () => {
    h.deleteSession.mockRejectedValue(new Error("nope"));
    const wrapper = await mountRoute();
    await wrapper.get('[data-testid="act-delete"]').trigger("click");
    await wrapper.get('[data-testid="confirm-delete"]').trigger("click");
    await flushPromises();
    expect(wrapper.get('[data-testid="confirm-delete"]').attributes("data-open")).toBe("true");
    expect(h.navigateMock).not.toHaveBeenCalled();
  });

  it("delete_is_ignored_when_capabilities_forbid_it", async () => {
    respond({ id: "session-1", capabilities: { ...capabilities, canDelete: false } });
    const wrapper = await mountRoute();
    await wrapper.get('[data-testid="act-delete"]').trigger("click");
    expect(wrapper.get('[data-testid="confirm-delete"]').attributes("data-open")).toBe("false");
    await wrapper.get('[data-testid="confirm-delete"]').trigger("click");
    expect(h.deleteSession).not.toHaveBeenCalled();
  });

  it("rename_calls_renameSession_with_the_id_the_title_and_a_callback", async () => {
    const wrapper = await mountRoute();
    await wrapper.get('[data-testid="header-rename"]').trigger("click");
    await flushPromises();
    expect(h.renameSession).toHaveBeenCalledWith("session-1", "New name", expect.any(Function));
  });

  it("archive_queues_the_session_when_allowed", async () => {
    const wrapper = await mountRoute();
    const archive = vi.spyOn(useArchiveQueueStore(), "archive").mockImplementation(() => undefined as never);
    await wrapper.get('[data-testid="act-archive"]').trigger("click");
    expect(archive).toHaveBeenCalledWith(["session-1"]);
  });

  it("archive_is_ignored_for_an_archived_session_which_offers_restore_instead", async () => {
    respond({ id: "session-1", retentionStatus: "archived" });
    const wrapper = await mountRoute();
    const store = useArchiveQueueStore();
    const archive = vi.spyOn(store, "archive").mockImplementation(() => undefined as never);
    const restore = vi.spyOn(store, "restore").mockResolvedValue(undefined as never);
    expect(wrapper.get('[data-testid="toolbar"]').attributes("data-can-archive")).toBe("false");
    expect(wrapper.get('[data-testid="toolbar"]').attributes("data-can-restore")).toBe("true");
    await wrapper.get('[data-testid="act-archive"]').trigger("click");
    expect(archive).not.toHaveBeenCalled();
    await wrapper.get('[data-testid="act-restore"]').trigger("click");
    await flushPromises();
    expect(restore).toHaveBeenCalledWith("session-1");
  });

  it("restore_is_ignored_for_a_session_that_is_not_archived", async () => {
    const wrapper = await mountRoute();
    const restore = vi.spyOn(useArchiveQueueStore(), "restore").mockResolvedValue(undefined as never);
    await wrapper.get('[data-testid="header-restore"]').trigger("click");
    expect(restore).not.toHaveBeenCalled();
  });

  it("restore_failure_is_swallowed", async () => {
    respond({ id: "session-1", retentionStatus: "archived" });
    const wrapper = await mountRoute();
    const restore = vi.spyOn(useArchiveQueueStore(), "restore").mockRejectedValue(new Error("nope"));
    await wrapper.get('[data-testid="header-restore"]').trigger("click");
    await flushPromises();
    expect(restore).toHaveBeenCalledTimes(1);
  });

  it("back_to_parent_navigates_with_the_parent_instance_when_the_parent_is_known", async () => {
    h.search.value = { instanceId: "instance-1", parentSessionId: "parent-1" };
    useSessionsStore().setSessions([listItem({ id: "parent-1", instanceId: "parent-instance" })]);
    const wrapper = await mountRoute();
    const back = wrapper.findAll("button").find((b) => b.text().includes("Back to parent"));
    await back!.trigger("click");
    expect(h.navigateMock).toHaveBeenCalledWith({
      to: "/sessions/$id", params: { id: "parent-1" }, search: { instanceId: "parent-instance", parentSessionId: undefined },
    });
  });

  it("back_to_parent_navigates_without_an_instance_when_the_parent_is_unknown", async () => {
    h.search.value = { instanceId: "instance-1", parentSessionId: "parent-2" };
    const wrapper = await mountRoute();
    const back = wrapper.findAll("button").find((b) => b.text().includes("Back to parent"));
    await back!.trigger("click");
    expect(h.navigateMock).toHaveBeenCalledWith({
      to: "/sessions/$id", params: { id: "parent-2" }, search: { instanceId: undefined, parentSessionId: undefined },
    });
  });
});
