import { beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, getActivePinia, setActivePinia } from "pinia";
import type { SessionPlan, SessionProgressDetail } from "@/lib/session-progress";
import { useSessionProgressStore } from "@/stores/session-progress";

vi.mock("@/lib/api-client", () => ({ apiFetch: vi.fn(() => Promise.resolve(new Response(null, { status: 204 }))) }));
vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: () => () => {},
  onReconnect: () => () => {},
}));

import ProgressCanvas from "@/components/canvas/ProgressCanvas.vue";

function step(number: string, title: string, checked: boolean, tickedAt: string | null = null) {
  return { key: number, number, title, checked, subDone: 0, subTotal: 0, tickedAt, tickedInMessageId: null };
}

const phasedPlan: SessionPlan = {
  path: ".weave/plans/thin-proxy.md",
  title: "Thin Proxy Simplification",
  trackedSince: "2026-09-13T11:09:00Z",
  groups: [
    { title: "Phase 1: Proxy", steps: [step("1", "Create `ISessionMessageProxy`", true), step("2", "Map messages", true, "2026-09-13T11:16:00Z")] },
    { title: "Phase 2: Delete dead code", steps: [step("3", "Remove the classes", true, "2026-09-13T12:19:00Z"), step("4", "Add migration to drop dead tables", false), step("5", "Remove the merge methods", false)] },
  ],
};

function detail(overrides: Partial<SessionProgressDetail> = {}): SessionProgressDetail {
  return {
    sessionId: "s1",
    kind: "plan",
    done: 3,
    total: 5,
    current: "Add migration to drop dead tables",
    todos: [
      { content: "Write DROP TABLE statements", status: "completed", priority: "high" },
      { content: "Drop the indexes", status: "in_progress", priority: "medium" },
    ],
    updatedAt: "2026-09-13T12:24:00Z",
    plan: phasedPlan,
    ...overrides,
  };
}

async function mountWith(progress: SessionProgressDetail | null) {
  const store = useSessionProgressStore();
  if (progress) store.apply(progress);
  else store.bySession = { s1: null };
  const wrapper = mount(ProgressCanvas, { props: { sessionId: "s1" }, global: { plugins: [getActivePinia()!] } });
  await flushPromises();
  return wrapper;
}

describe("ProgressCanvas", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it("shows the plan's title, file, count and one flow segment per group", async () => {
    const wrapper = await mountWith(detail());

    expect(wrapper.get(".progress-canvas__title").text()).toBe("Thin Proxy Simplification");
    expect(wrapper.get(".progress-canvas__source").text()).toContain(".weave/plans/thin-proxy.md");
    expect(wrapper.get(".progress-canvas__count").text()).toBe("3/5");
    expect(wrapper.findAll(".progress-canvas__segment")).toHaveLength(2);
    expect(wrapper.get(".progress-canvas__now").text()).toContain("Phase 2: Delete dead code");
    expect(wrapper.get(".progress-canvas__now").text()).toContain("next up: step 4");
  });

  it("folds finished groups and opens one on click", async () => {
    const wrapper = await mountWith(detail());

    const heads = wrapper.findAll(".progress-group__head");
    expect(heads[0]!.attributes("aria-expanded")).toBe("false");
    expect(heads[0]!.text()).toContain("2/2");
    expect(heads[1]!.attributes("aria-expanded")).toBe("true");
    expect(wrapper.findAll(".progress-step")).toHaveLength(3);

    await heads[0]!.trigger("click");

    expect(wrapper.findAll(".progress-step")).toHaveLength(5);
  });

  it("marks the current step and shows the live todo list under it", async () => {
    const wrapper = await mountWith(detail());

    const current = wrapper.get(".progress-step--current");
    expect(current.text()).toContain("Add migration to drop dead tables");
    expect(current.get(".progress-canvas__label").text()).toBe("Todos · 1 of 2");
    expect(current.findAll(".progress-todo").map((todo) => todo.text())).toEqual(["Write DROP TABLE statements", "Drop the indexes"]);
    expect(wrapper.findAll(".progress-step__detail")).toHaveLength(1);
  });

  it("renders backticked text in a step title as code", async () => {
    const wrapper = await mountWith(detail());
    await wrapper.findAll(".progress-group__head")[0]!.trigger("click");

    expect(wrapper.get(".progress-step__title code").text()).toBe("ISessionMessageProxy");
  });

  it("shows a flat plan without group headings", async () => {
    const flat: SessionPlan = {
      ...phasedPlan,
      groups: [{ title: "Tasks", steps: [step("1", "One", true), step("2", "Two", false), step("3", "Three", false)] }],
    };
    const wrapper = await mountWith(detail({ plan: flat, done: 1, total: 3 }));

    expect(wrapper.findAll(".progress-group__head")).toHaveLength(0);
    expect(wrapper.findAll(".progress-canvas__segment")).toHaveLength(3);
    expect(wrapper.findAll(".progress-step")).toHaveLength(3);
  });

  it("shows just the todo list when there's no plan", async () => {
    const wrapper = await mountWith(detail({ plan: null, kind: "todos", done: 1, total: 2 }));

    expect(wrapper.get(".progress-canvas__eyebrow").text()).toBe("Todos");
    expect(wrapper.findAll(".progress-todo")).toHaveLength(2);
    expect(wrapper.get(".progress-canvas__note").text()).toContain("hasn't written a markdown checklist");
  });

  it("explains where progress comes from when there's nothing yet", async () => {
    const wrapper = await mountWith(null);

    expect(wrapper.get(".progress-canvas__empty").text()).toContain("Nothing to track yet");
    expect(wrapper.get(".progress-canvas__empty").text()).toContain(".weave/plans/");
  });
});
