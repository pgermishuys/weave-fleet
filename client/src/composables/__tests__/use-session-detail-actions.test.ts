import { flushPromises } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { shallowRef } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { confirmAction, deleteSession } = vi.hoisted(() => ({ confirmAction: vi.fn(), deleteSession: vi.fn() }));
vi.mock("@/components/ui/confirm", () => ({ confirmAction }));
vi.mock("@/composables/use-session-actions", async (importOriginal) => {
  const { shallowRef: ref } = await import("vue");
  return {
    ...(await importOriginal<typeof import("@/composables/use-session-actions")>()),
    useDeleteSession: () => ({ deleteSession, isDeleting: ref(false), error: ref(undefined) }),
  };
});

import { useSessionDetailActions } from "@/composables/use-session-detail-actions";

function actions(goHome = vi.fn()) {
  return useSessionDetailActions({
    sessionId: () => "session-1",
    instanceId: () => "instance-1",
    canAbort: () => true,
    canFork: () => true,
    canDelete: () => true,
    canArchive: () => true,
    canRestore: () => true,
    sessionTitle: () => "Fix the login bug",
    remoteSession: shallowRef(null),
    optimisticRetentionStatus: () => null,
    onSessionStateChanged: vi.fn(),
    goHome,
  });
}

describe("useSessionDetailActions: delete", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    confirmAction.mockReset();
    deleteSession.mockReset();
  });

  it("asks first, naming the session, with the delete run inside the confirm, then goes home", async () => {
    confirmAction.mockImplementation(async (options: { run: () => Promise<unknown> }) => {
      await options.run();
      return true;
    });
    deleteSession.mockResolvedValue(undefined);
    const goHome = vi.fn();

    await actions(goHome).handleDelete();
    await flushPromises();

    expect(confirmAction).toHaveBeenCalledWith(expect.objectContaining({
      title: "Delete this session?",
      subject: "Fix the login bug",
      confirmLabel: "Delete session",
      destructive: true,
    }));
    expect(deleteSession).toHaveBeenCalledWith("session-1", "instance-1");
    expect(goHome).toHaveBeenCalledOnce();
  });

  it("stays on the session when the confirm is cancelled", async () => {
    confirmAction.mockResolvedValue(false);
    const goHome = vi.fn();

    await actions(goHome).handleDelete();

    expect(deleteSession).not.toHaveBeenCalled();
    expect(goHome).not.toHaveBeenCalled();
  });
});
