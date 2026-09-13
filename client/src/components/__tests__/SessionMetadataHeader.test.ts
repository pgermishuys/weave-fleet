import { beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, getActivePinia, setActivePinia } from "pinia";
import type { SessionProgressDetail } from "@/lib/session-progress";
import { useCanvasesStore } from "@/stores/canvases";
import { useSessionProgressStore } from "@/stores/session-progress";

vi.mock("@/lib/api-client", () => ({ apiFetch: vi.fn(() => Promise.resolve(new Response(null, { status: 204 }))) }));
vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: () => () => {},
  onReconnect: () => () => {},
}));

import SessionMetadataHeader from "@/components/session/SessionMetadataHeader.vue";

function planProgress(): SessionProgressDetail {
  return {
    sessionId: "s1",
    kind: "plan",
    done: 1,
    total: 3,
    current: "Map `SessionSnapshot`",
    todos: [],
    updatedAt: "2026-09-13T12:00:00Z",
    subagents: [],
    plan: {
      path: "plan.md",
      title: "Proxy",
      trackedSince: "2026-09-13T11:00:00Z",
      groups: [{
        title: "Phase 1: Proxy",
        steps: [
          { key: "1", number: "1", title: "Create it", checked: true, subDone: 0, subTotal: 0, tickedAt: null, tickedInMessageId: null },
          { key: "2", number: "2", title: "Map `SessionSnapshot`", checked: false, subDone: 0, subTotal: 0, tickedAt: null, tickedInMessageId: null },
          { key: "3", number: "3", title: "Ship it", checked: false, subDone: 0, subTotal: 0, tickedAt: null, tickedInMessageId: null },
        ],
      }],
    },
  };
}

async function mountStrip(progress: SessionProgressDetail | null) {
  const store = useSessionProgressStore();
  if (progress) store.apply(progress);
  else store.bySession = { s1: null };
  const wrapper = mount(SessionMetadataHeader, { props: { sessionId: "s1" }, global: { plugins: [getActivePinia()!] } });
  await flushPromises();
  return wrapper;
}

describe("SessionMetadataHeader", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it("sums up the plan: its current group, the next step and the count", async () => {
    const wrapper = await mountStrip(planProgress());

    expect(wrapper.get(".progress-strip__heading").text()).toBe("Phase 1: Proxy");
    expect(wrapper.get(".progress-strip__current").text()).toBe("Next: 2. Map SessionSnapshot");
    expect(wrapper.get(".progress-strip__count").text()).toBe("1/3");
  });

  it("sums up a todo list when there's no plan", async () => {
    const wrapper = await mountStrip({ ...planProgress(), plan: null, kind: "todos", current: "Drop the indexes", done: 2, total: 4 });

    expect(wrapper.get(".progress-strip__heading").text()).toBe("Todos");
    expect(wrapper.get(".progress-strip__current").text()).toBe("Drop the indexes");
    expect(wrapper.get(".progress-strip__count").text()).toBe("2/4");
  });

  it("opens the Progress tab on click, and hides while it's open", async () => {
    const wrapper = await mountStrip(planProgress());
    const canvases = useCanvasesStore();

    await wrapper.get(".progress-strip").trigger("click");

    expect(canvases.sessionCanvases("s1").activeId).toBe("progress");
    expect(wrapper.find(".progress-strip").exists()).toBe(false);
  });

  it("shows nothing without progress", async () => {
    const wrapper = await mountStrip(null);

    expect(wrapper.find(".progress-strip").exists()).toBe(false);
  });
});
