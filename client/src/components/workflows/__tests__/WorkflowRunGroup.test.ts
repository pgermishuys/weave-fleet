import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";

vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: () => () => {} }));

import WorkflowRunGroup from "@/components/workflows/WorkflowRunGroup.vue";
import type { SessionListItem } from "@/api/client";
import { buildRun } from "./workflow-fixtures";

const SessionItemStub = { name: "SessionItem", props: ["session", "label", "active", "stepNote"], template: "<div class=\"step\">{{ label }} {{ stepNote }}</div>" };

function item(id: string): SessionListItem {
  return { session: { id, title: `Keyboard shortcut sheet · ${id}` } } as unknown as SessionListItem;
}

describe("WorkflowRunGroup", () => {
  it("says With you on the run and on the step the user finishes, not Needs you", () => {
    const wrapper = mount(WorkflowRunGroup, {
      props: { run: buildRun(), steps: [{ session: item("s1"), label: "Design" }], activeSessionId: null },
      global: { stubs: { SessionItem: SessionItemStub } },
    });

    expect(wrapper.get(".wf-group__meta").text()).toBe("With you");
    expect(wrapper.get(".wf-group__meta").classes()).toContain("wf-group__meta--with");
    expect(wrapper.get(".step").text()).toBe("Design With you");
  });

  it("says nothing extra on a step the agent finishes", () => {
    const wrapper = mount(WorkflowRunGroup, {
      props: { run: buildRun({ withYou: null }), steps: [{ session: item("s1"), label: "Design" }], activeSessionId: null },
      global: { stubs: { SessionItem: SessionItemStub } },
    });

    expect(wrapper.get(".wf-group__meta").text()).toBe("Working");
    expect(wrapper.get(".step").text()).toBe("Design");
  });
});
