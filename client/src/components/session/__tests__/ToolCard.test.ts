import { mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import ToolCard from "@/components/session/ToolCard.vue";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";

describe("ToolCard", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it("renders_stable_test_ids_for_summary_output_and_empty_state", async () => {
    const pinia = createPinia();
    const wrapper = mount(ToolCard, {
      global: { plugins: [pinia] },
      props: {
        id: "tool-card-1",
        title: "Bash",
        kind: "bash",
        status: "Completed",
        summary: "Completed command execution",
        output: "line 1\nline 2",
      },
    });

    expect(wrapper.get('[data-testid="tool-card"]').attributes("data-tool-card-id")).toBe("tool-card-1");
    expect(wrapper.find('[data-testid="tool-card-header"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="tool-card-body"]').exists()).toBe(true);
    expect(wrapper.get('[data-testid="tool-card-summary"]').text()).toBe("Completed command execution");
    expect(wrapper.get('[data-testid="tool-card-output"]').text()).toContain("line 1");
    expect(wrapper.find('[data-testid="tool-card-empty-state"]').exists()).toBe(false);
  });

  it("renders_empty_state_with_stable_selector_when_tool_has_no_content", () => {
    const pinia = createPinia();
    const wrapper = mount(ToolCard, {
      global: { plugins: [pinia] },
      props: {
        id: "tool-card-empty",
        title: "Read",
      },
    });

    expect(wrapper.get('[data-testid="tool-card-empty-state"]').text()).toBe("No output captured");
  });

  it("shows_diff_rows_with_stable_selectors_when_inline_diffs_are_enabled", async () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    useWorkspaceUiStore(pinia).setInlineToolDiffs(true);

    const wrapper = mount(ToolCard, {
      global: { plugins: [pinia] },
      props: {
        id: "tool-card-diff",
        title: "Edit",
        kind: "edit",
        status: "Completed",
        initiallyCollapsed: true,
        diffLines: [
          { type: "context", content: "@@ -1,2 +1,2 @@" },
          { type: "remove", content: "-old line", oldLineNumber: 1 },
          { type: "add", content: "+new line", newLineNumber: 1 },
        ],
      },
    });

    const diffRows = wrapper.findAll('[data-testid="tool-card-diff-row"]');

    expect(wrapper.find('[data-testid="tool-card-body"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="tool-card-diff"]').exists()).toBe(true);
    expect(diffRows).toHaveLength(3);
    expect(diffRows[0]?.attributes("data-diff-type")).toBe("context");
    expect(diffRows[1]?.attributes("data-diff-type")).toBe("remove");
    expect(diffRows[2]?.attributes("data-diff-type")).toBe("add");
  });
});
