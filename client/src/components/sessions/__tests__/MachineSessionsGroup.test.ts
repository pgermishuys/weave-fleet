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

  it("puts forks and started sessions under the session they came from, and counts what runs", () => {
    const item = (id: string, title: string, extra: Record<string, unknown> = {}) => ({
      instanceId: id, workspaceId: "w", workspaceDirectory: "/repo", workspaceDisplayName: null, isolationStrategy: "existing",
      sessionStatus: "idle", session: { id, title, time: { created: 1, updated: 1 }, tags: [] }, instanceStatus: "running",
      lifecycleStatus: "running", retentionStatus: "active", typedInstanceStatus: "running", isHidden: false, tags: [], ...extra,
    });
    const state = {
      ...listed,
      sessions: [
        item("fork", "Fork of the mapper", { forkedFromSessionId: "parent", spawnKind: "fork" }),
        item("parent", "Capture subagents", { runningWorkCount: 2 }),
        item("other", "Something else"),
      ],
    };

    const view = mount(MachineSessionsGroup, { props: { machine: macbook, state, query: "" } });
    const rows = view.findAll("[data-testid='machine-session-row']");

    expect(rows.map((row) => row.attributes("data-session-id"))).toEqual(["parent", "fork", "other"]);
    expect(rows[0]!.get(".machine-row__running").text()).toBe("2");
    expect(rows[1]!.classes()).toContain("machine-row--child");
    expect(rows[1]!.get(".machine-row__kind").text()).toBe("fork");
    expect(rows[2]!.classes()).not.toContain("machine-row--child");
  });
});
