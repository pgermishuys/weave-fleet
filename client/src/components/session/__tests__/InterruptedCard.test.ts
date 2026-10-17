import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import InterruptedCard from "@/components/session/InterruptedCard.vue";
import type { SessionInterruption } from "@/lib/session-interruption";

function interruption(overrides: Partial<SessionInterruption> = {}): SessionInterruption {
  return { at: "2026-10-17T14:02:00Z", waiting: false, lastCall: "bash: bun run build", changedFiles: 3, ...overrides };
}

describe("InterruptedCard", () => {
  it("says what was working, the last call and the changed files", () => {
    const wrapper = mount(InterruptedCard, { props: { interruption: interruption() } });

    expect(wrapper.text()).toContain("Cut off by a restart");
    expect(wrapper.get('[data-testid="interrupted-text"]').text()).toContain("while this was working");
    expect(wrapper.get('[data-testid="interrupted-last-call"]').text()).toContain("bash: bun run build");
    expect(wrapper.get('[data-testid="interrupted-files"]').text()).toContain("3 files changed in this worktree");
    expect(wrapper.get('[data-testid="interrupted-continue"]').text()).toBe("Continue");
    expect(wrapper.text()).not.toContain("Always carry on");
  });

  it("leaves out the call and the files when Fleet doesn't know them", () => {
    const wrapper = mount(InterruptedCard, { props: { interruption: interruption({ lastCall: null, changedFiles: null }) } });

    expect(wrapper.find('[data-testid="interrupted-last-call"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="interrupted-files"]').exists()).toBe(false);
  });

  it("says the question was lost and offers Ask again", () => {
    const wrapper = mount(InterruptedCard, { props: { interruption: interruption({ waiting: true }) } });

    expect(wrapper.get('[data-testid="interrupted-text"]').text()).toContain("The question was lost");
    expect(wrapper.get('[data-testid="interrupted-continue"]').text()).toBe("Ask again");
    expect(wrapper.find('[data-testid="interrupted-last-call"]').exists()).toBe(false);
  });

  it("calls the handlers, and disables the buttons while busy", async () => {
    const wrapper = mount(InterruptedCard, { props: { interruption: interruption() } });
    await wrapper.get('[data-testid="interrupted-continue"]').trigger("click");
    await wrapper.get('[data-testid="interrupted-dismiss"]').trigger("click");
    expect(wrapper.emitted("continue")).toHaveLength(1);
    expect(wrapper.emitted("dismiss")).toHaveLength(1);

    await wrapper.setProps({ busy: true });
    expect(wrapper.get('[data-testid="interrupted-continue"]').attributes("disabled")).toBeDefined();
  });
});
