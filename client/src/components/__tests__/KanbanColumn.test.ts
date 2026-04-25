import { mount, type VueWrapper } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import KanbanColumn from "@/components/board/KanbanColumn.vue";
import type { BoardCard, BoardLane } from "@/stores/board";

function createLaneFixture(overrides: Partial<BoardLane> = {}): BoardLane {
  return {
    id: "lane-1",
    boardId: "board-1",
    name: "Backlog",
    position: 1_024,
    isInbox: false,
    createdAt: "2026-04-24T10:00:00Z",
    updatedAt: "2026-04-24T10:00:00Z",
    ...overrides,
  };
}

function createCardFixture(overrides: Partial<BoardCard> = {}): BoardCard {
  return {
    id: "card-1",
    boardId: "board-1",
    laneId: "lane-1",
    title: "Write regression coverage",
    sourceType: null,
    sourceKey: "manual-1",
    metadata: null,
    position: 1_024,
    archivedAt: null,
    createdAt: "2026-04-24T10:01:00Z",
    updatedAt: "2026-04-24T10:01:00Z",
    ...overrides,
  };
}

function mountColumn(overrides: Partial<InstanceType<typeof KanbanColumn>["$props"]> = {}): VueWrapper {
  return mount(KanbanColumn, {
    props: {
      lane: createLaneFixture(),
      cards: [createCardFixture()],
      draggedCardId: null,
      isMutating: false,
      isManageMode: false,
      canMoveLeft: false,
      canMoveRight: true,
      ...overrides,
    },
  });
}

function getButtonByText(wrapper: VueWrapper, label: string) {
  const button = wrapper.findAll("button").find((candidate) => candidate.text() === label);
  if (!button) {
    throw new Error(`Unable to find button with label: ${label}`);
  }

  return button;
}

describe("KanbanColumn", () => {
  it("hides manage-only lane actions in work mode while still allowing card creation", async () => {
    const wrapper = mountColumn();

    expect(wrapper.find(".kanban-col__lane-actions").exists()).toBe(false);
    expect(wrapper.text()).not.toContain("Make inbox");
    expect(wrapper.text()).not.toContain("←");
    expect(wrapper.text()).not.toContain("→");

    await getButtonByText(wrapper, "+ Add a card").trigger("click");

    expect(wrapper.find(".kanban-col__composer-form").exists()).toBe(true);
    expect(wrapper.find(".kanban-col__composer-input").exists()).toBe(true);
  });

  it("shows lane management controls in manage mode", () => {
    const wrapper = mountColumn({ isManageMode: true });

    expect(wrapper.find(".kanban-col__lane-actions").exists()).toBe(true);
    expect(wrapper.text()).toContain("Rename");
    expect(wrapper.text()).toContain("Make inbox");
    expect(wrapper.text()).toContain("Delete");
  });

  it("cancels lane rename state when switching from manage mode back to work mode", async () => {
    const wrapper = mountColumn({ isManageMode: true });

    await getButtonByText(wrapper, "Rename").trigger("click");
    await wrapper.get(".kanban-col__rename-input").setValue("Review");

    await wrapper.setProps({ isManageMode: false });

    expect(wrapper.find(".kanban-col__rename-form").exists()).toBe(false);
    expect(wrapper.get(".kanban-col__title").text()).toBe("Backlog");

    await wrapper.setProps({ isManageMode: true });
    await getButtonByText(wrapper, "Rename").trigger("click");

    expect((wrapper.get(".kanban-col__rename-input").element as HTMLInputElement).value).toBe("Backlog");
  });
});
