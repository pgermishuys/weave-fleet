import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import MessageBubble from "@/components/session/MessageBubble.vue";

function bubble(body: string, sessionReferences?: { token: string; sessionId: string; title: string }[]) {
  return mount(MessageBubble, {
    props: { author: "You", role: "user", showIdentity: true, clusterPosition: "single", body, sessionReferences },
  });
}

describe("MessageBubble with @ sessions", () => {
  const references = [{ token: "@t3code-what-can-we-learn", sessionId: "ses-a", title: "t3code: what can we learn?" }];

  it("shows each referenced session as a chip with its title", () => {
    const wrapper = bubble("Use the subagent mapping from @t3code-what-can-we-learn", references);

    const chip = wrapper.get(".session-ref-chip");
    expect(chip.text()).toBe("t3code: what can we learn?");
    expect(chip.attributes("href")).toBe("/sessions/ses-a");
    expect(wrapper.text()).not.toContain("@t3code-what-can-we-learn");
  });

  it("opens the session when its chip is clicked", async () => {
    const wrapper = bubble("From @t3code-what-can-we-learn", references);

    await wrapper.get(".session-ref-chip__title").trigger("click");

    expect(wrapper.emitted("open-session")).toEqual([["ses-a"]]);
  });

  it("leaves a message without references as text", () => {
    const wrapper = bubble("Mail me@example.com about @t3code-what-can-we-learn");

    expect(wrapper.find(".session-ref-chip").exists()).toBe(false);
    expect(wrapper.text()).toContain("@t3code-what-can-we-learn");
  });
});
