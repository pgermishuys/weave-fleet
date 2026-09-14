import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import RecapLine from "@/components/session/RecapLine.vue";

describe("RecapLine", () => {
  it("shows the recap in Claude Code's recap: form", () => {
    const wrapper = mount(RecapLine, {
      props: { recap: { sessionId: "s1", text: "You're adding live progress. Next, decide Y.", writtenAt: "2026-09-13T14:39:00Z" } },
    });

    const line = wrapper.get("[data-testid='recap-line']");
    expect(line.attributes("role")).toBe("note");
    expect(line.text()).toBe("recap: You're adding live progress. Next, decide Y.");
  });

  it("renders nothing without a recap", () => {
    expect(mount(RecapLine, { props: { recap: null } }).find("[data-testid='recap-line']").exists()).toBe(false);
    expect(mount(RecapLine, { props: { recap: { sessionId: "s1", text: null } } }).find("[data-testid='recap-line']").exists()).toBe(false);
  });
});
