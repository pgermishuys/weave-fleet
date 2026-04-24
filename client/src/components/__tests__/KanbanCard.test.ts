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

function createGitHubMetadata(overrides: Record<string, unknown> = {}): string {
  return JSON.stringify({
    number: 42,
    state: "open",
    labels: ["bug", "urgent"],
    assignee: "hubot",
    html_url: "https://github.com/acme/rocket/issues/42",
    updated_at: "2026-02-27T10:03:00Z",
    ...overrides,
  });
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
    expect(wrapper.get(".k-card").classes()).not.toContain("k-card--synced");
    expect(wrapper.get(".k-card").attributes("draggable")).toBe("true");
  });

  it("renders synced GitHub card treatment with outbound link", () => {
    const wrapper = mount(KanbanCard, {
      props: {
        card: createBoardCard({
          sourceType: "github",
          sourceKey: "github:acme/rocket#42",
          metadata: createGitHubMetadata(),
        }),
        isDragging: false,
        isMutating: false,
      },
    });

    expect(wrapper.get(".k-card").classes()).toContain("k-card--synced");
    expect(wrapper.get(".k-card").classes()).not.toContain("k-card--manual");
    expect(wrapper.get(".k-card__source-pill").text()).toBe("GitHub sync");
    expect(wrapper.get(".k-card__sync-pill").text()).toContain("Issue #42");
    expect(wrapper.get(".k-card__github-link").attributes("href")).toBe("https://github.com/acme/rocket/issues/42");
    expect(wrapper.text()).toContain("open");
    expect(wrapper.text()).toContain("hubot");
    expect(wrapper.text()).toContain("bug");
    expect(wrapper.text()).toContain("urgent");
  });

  it("shows stale indicator for stale synced cards", () => {
    const wrapper = mount(KanbanCard, {
      props: {
        card: createBoardCard({
          sourceType: "github",
          sourceKey: "github:acme/rocket#42",
          metadata: createGitHubMetadata({ stale: true }),
        }),
        isDragging: false,
        isMutating: false,
      },
    });

    expect(wrapper.get(".k-card__stale-pill").text()).toContain("Stale");
    expect(wrapper.get(".k-card__stale-pill").attributes("aria-label")).toBe("Sync is stale");
  });
});
