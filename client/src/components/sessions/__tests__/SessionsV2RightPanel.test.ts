import { mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h, ref, shallowRef } from "vue";
import SessionsV2RightPanel from "@/components/sessions/SessionsV2RightPanel.vue";
import { provideSessionDiffsContext } from "@/composables/use-session-diffs-context";
import type { UseDiffsResult } from "@/composables/use-diffs";

const { useDiffsMock, subscribed } = vi.hoisted(() => ({ useDiffsMock: vi.fn(), subscribed: [] as string[] }));

function fakeDiffs(): UseDiffsResult {
  return {
    diffs: ref([]), byFile: ref(new Map()), available: ref(true), base: ref(null), isLoading: ref(false),
    isStale: ref(false), error: ref(undefined), fetchDiffs: vi.fn(async () => {}), markStale: vi.fn(),
  } as unknown as UseDiffsResult;
}

vi.mock("@/composables/use-diffs", () => ({ useDiffs: useDiffsMock }));
vi.mock("@/composables/use-session-progress", () => ({ useSessionProgress: () => ({ progress: ref(null) }) }));
vi.mock("@/composables/use-session-lineage", () => ({ useSessionLineage: () => ({ activeCount: ref(0) }) }));
vi.mock("@/composables/use-server-canvases", () => ({ useServerCanvases: vi.fn() }));
vi.mock("@/composables/use-file-live-updates", () => ({ useFileLiveUpdates: vi.fn() }));
vi.mock("@/composables/use-send-prompt", () => ({ useSendPrompt: () => ({ sendPrompt: vi.fn() }) }));
vi.mock("@/composables/use-draft-state", () => ({ useDraftState: () => ({ draft: {}, setText: vi.fn() }) }));
vi.mock("@/composables/use-annotation", () => ({ useAnnotation: () => ({ annotation: shallowRef(null) }) }));
vi.mock("@/stores/smart-links", () => ({ useSmartLinksStore: () => ({ ensureLoaded: vi.fn(), visibleLinks: () => [] }) }));

const stubs = { CanvasHost: true, CollapsedRightRail: true, SessionMetadataHeader: true, AnnotationPopover: true };

describe("SessionsV2RightPanel diffs", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    useDiffsMock.mockReset();
    subscribed.length = 0;
    useDiffsMock.mockImplementation((id: () => string | null) => {
      const diffs = fakeDiffs();
      const original = diffs.fetchDiffs as ReturnType<typeof vi.fn>;
      original.mockImplementation(async () => { subscribed.push(String(id())); });
      return diffs;
    });
  });

  it("does not run its own diffs instance when the session's route already holds one", () => {
    const routeDiffs = fakeDiffs();
    const Parent = defineComponent({
      setup() {
        provideSessionDiffsContext(routeDiffs);
        return () => h(SessionsV2RightPanel, { sessionId: "s1" });
      },
    });
    const wrapper = mount(Parent, { global: { stubs } });

    // The panel's own instance is told there is no session, so it neither fetches nor listens.
    const getter = useDiffsMock.mock.calls[0]![0] as () => string | null;
    expect(getter()).toBeNull();
    expect(routeDiffs.fetchDiffs).toHaveBeenCalledTimes(1);
    wrapper.unmount();
  });

  it("runs its own diffs instance when no route holds one", () => {
    mount(SessionsV2RightPanel, { props: { sessionId: "s1" }, global: { stubs } });
    const getter = useDiffsMock.mock.calls[0]![0] as () => string | null;
    expect(getter()).toBe("s1");
  });
});
