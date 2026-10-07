import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import CompactionDivider from "@/components/session/CompactionDivider.vue";

describe("CompactionDivider", () => {
  it("says where the context was compacted, how big it was before and after, and keeps the summary behind a toggle", async () => {
    const wrapper = mount(CompactionDivider, {
      props: { compaction: { trigger: "manual", tokensBefore: 181_000, tokensAfter: 34_000, summary: "**Goal:** fix the tests." } },
    });

    expect(wrapper.get('[data-testid="compaction-divider"]').attributes("role")).toBe("separator");
    expect(wrapper.text()).toContain("Context compacted · 181k → 34k tokens");
    expect(wrapper.find('[data-testid="compaction-summary"]').exists()).toBe(false);

    const toggle = wrapper.get('[data-testid="compaction-summary-toggle"]');
    expect(toggle.text()).toBe("Show summary");
    await toggle.trigger("click");

    expect(wrapper.get('[data-testid="compaction-summary"]').html()).toContain("<strong>Goal:</strong>");
    expect(toggle.text()).toBe("Hide summary");
    expect(toggle.attributes("aria-expanded")).toBe("true");
  });

  it("is just the line when the harness gives no sizes or summary", () => {
    const wrapper = mount(CompactionDivider, { props: { compaction: { trigger: "auto" } } });

    expect(wrapper.text().trim()).toBe("Context compacted");
    expect(wrapper.find('[data-testid="compaction-summary-toggle"]').exists()).toBe(false);
  });
});
