import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import MachineSessionsGroup from "@/components/sessions/MachineSessionsGroup.vue";
import type { MachineEntry } from "@/stores/machines";

const macbook: MachineEntry = {
  key: "m-mac",
  name: "macbook",
  os: "macos",
  baseUrl: "http://mac.test:2113",
  isHome: false,
  isLive: false,
  connection: null,
};

const listed = { sessions: [], error: null, loadedAt: 1, loading: false };

describe("MachineSessionsGroup", () => {
  it("shows the new-session draft that starts on this machine, first, and opens it", async () => {
    const view = mount(MachineSessionsGroup, {
      props: {
        machine: macbook,
        state: listed,
        query: "",
        draft: { key: "new-session-draft-1", title: "Fix the sign-in loop", projectId: null, isStarting: false },
        draftActive: true,
      },
    });

    const row = view.get("[data-testid='new-session-draft-row']");
    expect(row.text()).toContain("Fix the sign-in loop");
    expect(row.attributes("aria-current")).toBe("true");
    expect(view.text()).not.toContain("No sessions");

    await row.trigger("click");
    expect(view.emitted("openDraft")).toHaveLength(1);
  });

  it("has no draft row for a draft on another machine", () => {
    const view = mount(MachineSessionsGroup, { props: { machine: macbook, state: listed, query: "" } });

    expect(view.find("[data-testid='new-session-draft-row']").exists()).toBe(false);
    expect(view.text()).toContain("No sessions");
  });
});
