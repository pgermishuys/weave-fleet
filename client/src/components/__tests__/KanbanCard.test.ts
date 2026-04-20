import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import KanbanCard from "@/components/board/KanbanCard.vue";
import type { BoardSession } from "@/stores/board";

function createBoardSession(overrides: Partial<BoardSession> = {}): BoardSession {
  return {
    id: "session-1",
    title: "Ship the Vue migration",
    projectName: "Frontend",
    projectColor: "#ff00aa",
    status: "error",
    agent: "shuttle",
    modelName: "GPT-5.4 Mini",
    createdAt: new Date("2026-02-27T10:00:00Z"),
    completedAt: new Date("2026-02-27T10:05:00Z"),
    prompt: "Migrate the board to Vue",
    totalTokens: 12_300,
    cost: 1.25,
    durationSeconds: 125,
    progressPercent: 82,
    progressLabel: "9/11 tasks",
    ...overrides,
  };
}

describe("KanbanCard", () => {
  it("renders the session summary and formatted metrics", () => {
    const wrapper = mount(KanbanCard, {
      props: {
        session: createBoardSession(),
      },
    });

    expect(wrapper.get(".k-card__title").text()).toBe("Ship the Vue migration");
    expect(wrapper.get(".k-card__status").text()).toBe("Error");
    expect(wrapper.text()).toContain("Frontend");
    expect(wrapper.text()).toContain("shuttle");
    expect(wrapper.text()).toContain("GPT-5.4 Mini");
    expect(wrapper.text()).toContain("9/11 tasks");
    expect(wrapper.text()).toContain("82%");
    expect(wrapper.text()).toContain("12.3k");
    expect(wrapper.text()).toContain("2m 5s");
    expect(wrapper.text()).toContain("$1.25");
  });

  it("applies status and progress styles for failed cards", () => {
    const wrapper = mount(KanbanCard, {
      props: {
        session: createBoardSession(),
      },
    });

    expect(wrapper.get(".k-card").classes()).toContain("failed-card");
    expect(wrapper.get(".k-card").attributes("style")).toContain("border-left-color: var(--error)");
    expect(wrapper.get(".k-progress").attributes("style")).toContain("width: 82%");
    expect(wrapper.get(".k-progress").attributes("style")).toContain("background-color: var(--error)");
    expect(wrapper.get(".k-card__project-dot").attributes("style")).toContain("background-color: rgb(255, 0, 170)");
  });
});
