import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import DiffView from "@/components/session/DiffView.vue";
import type { DiffLine } from "@/lib/diff-parser";

function createContextLine(lineNumber: number): DiffLine {
  return {
    type: "context",
    content: `unchanged ${lineNumber}`,
    oldLineNumber: lineNumber,
    newLineNumber: lineNumber,
  };
}

describe("DiffView", () => {
  it("renders_line_number_gutters_markers_and_line_type_metadata", () => {
    const wrapper = mount(DiffView, {
      props: {
        lines: [
          { type: "context", content: "const value = 1;", oldLineNumber: 1, newLineNumber: 1 },
          { type: "remove", content: "const removed = true;", oldLineNumber: 2 },
          { type: "add", content: "const added = true;", newLineNumber: 2 },
        ] satisfies DiffLine[],
      },
    });

    const rows = wrapper.findAll('[data-testid="tool-card-diff-row"]');

    expect(rows).toHaveLength(3);
    expect(rows.map((row) => row.attributes("data-diff-type"))).toEqual(["context", "remove", "add"]);
    expect(rows[0]?.classes()).toContain("diff-line--context");
    expect(rows[1]?.classes()).toContain("diff-line--remove");
    expect(rows[2]?.classes()).toContain("diff-line--add");
    expect(rows[1]?.find(".diff-line__marker").text()).toBe("-");
    expect(rows[2]?.find(".diff-line__marker").text()).toBe("+");
    expect(rows[0]?.find(".diff-line__number--old").text()).toBe("1");
    expect(rows[0]?.find(".diff-line__number--new").text()).toBe("1");
    expect(rows[1]?.find(".diff-line__number--old").text()).toBe("2");
    expect(rows[1]?.find(".diff-line__number--new").text()).toBe("");
    expect(rows[2]?.find(".diff-line__number--old").text()).toBe("");
    expect(rows[2]?.find(".diff-line__number--new").text()).toBe("2");
  });

  it("collapses_large_context_runs_and_expands_them_on_request", async () => {
    const wrapper = mount(DiffView, {
      props: {
        lines: Array.from({ length: 12 }, (_, index) => createContextLine(index + 1)),
      },
    });

    expect(wrapper.findAll('[data-testid="tool-card-diff-row"]')).toHaveLength(6);
    expect(wrapper.text()).toContain("Show 6 unchanged lines");
    expect(wrapper.text()).not.toContain("unchanged 4");

    await wrapper.get(".diff-expand__button").trigger("click");

    expect(wrapper.find(".diff-expand__button").exists()).toBe(false);
    expect(wrapper.findAll('[data-testid="tool-card-diff-row"]')).toHaveLength(12);
    expect(wrapper.text()).toContain("unchanged 4");
  });
});
