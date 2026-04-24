import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import KanbanCard from "@/components/board/KanbanCard.vue";
import type { BoardCard } from "@/stores/board";

function createBoardCard(overrides: Partial<BoardCard> = {}): BoardCard {
  return {
    id: "card-1",
    boardId: "board-1",
    laneId: "lane-1",
    title: "Ship the Vue migration",
    sourceType: null,
    sourceKey: "manual-task-1",
    metadata: "Migrate the board to Vue",
    position: 1024,
    archivedAt: null,
    createdAt: "2026-02-27T10:00:00Z",
    updatedAt: "2026-02-27T10:05:00Z",
    ...overrides,
  };
}

describe("KanbanCard", () => {
  it("renders the card summary and metadata", () => {
    const wrapper = mount(KanbanCard, {
      props: {
        card: createBoardCard(),
        isDragging: false,
        isMutating: false,
      },
    });

    expect(wrapper.get(".k-card__title").text()).toBe("Ship the Vue migration");
    expect(wrapper.get(".k-card__source-pill").text()).toBe("Manual");
    expect(wrapper.text()).toContain("Migrate the board to Vue");
    expect(wrapper.text()).toContain("manual-task-1");
    expect(wrapper.text()).toContain("1024");
    expect(wrapper.text()).toContain("Updated");
  });

  it("applies drag and manual styling", () => {
    const wrapper = mount(KanbanCard, {
      props: {
        card: createBoardCard(),
        isDragging: true,
        isMutating: false,
      },
    });

    expect(wrapper.get(".k-card").classes()).toContain("k-card--manual");
    expect(wrapper.get(".k-card").classes()).toContain("k-card--dragging");
    expect(wrapper.get(".k-card").attributes("draggable")).toBe("true");
  });
});
