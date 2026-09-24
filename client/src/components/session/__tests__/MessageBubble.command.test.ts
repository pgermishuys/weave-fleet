import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import MessageBubble from "@/components/session/MessageBubble.vue";

const template = "Tidy up the code in src/auth without changing what it does.\n\n1. Read every file in that area.";

function bubble(props: { body: string; command?: { name: string; arguments?: string | null } }) {
  return mount(MessageBubble, {
    props: {
      author: "You",
      role: "user",
      showIdentity: true,
      clusterPosition: "single",
      ...props,
    },
  });
}

describe("MessageBubble with a slash command", () => {
  it("shows the command, not the prompt it expanded to", () => {
    const wrapper = bubble({ body: template, command: { name: "tidy", arguments: "src/auth" } });

    expect(wrapper.get("[data-testid='message-command']").text()).toBe("/tidy src/auth");
    expect(wrapper.text()).not.toContain("Read every file");
    expect(wrapper.find("[data-testid='message-command-prompt']").exists()).toBe(false);
  });

  it("shows the expanded prompt behind Show prompt", async () => {
    const wrapper = bubble({ body: template, command: { name: "tidy", arguments: "src/auth" } });

    const toggle = wrapper.get("[data-testid='message-command-toggle']");
    expect(toggle.text()).toBe("Show prompt");
    await toggle.trigger("click");

    expect(wrapper.get("[data-testid='message-command-prompt']").text()).toContain("Read every file in that area.");
    expect(toggle.text()).toBe("Hide prompt");
    expect(toggle.attributes("aria-expanded")).toBe("true");
  });

  it("offers no prompt when the message is the command itself", () => {
    // Fleet's own message while the harness works, or a harness (Claude Code) that expands the command itself.
    const wrapper = bubble({ body: "/tidy src/auth", command: { name: "tidy", arguments: "src/auth" } });

    expect(wrapper.get("[data-testid='message-command']").text()).toBe("/tidy src/auth");
    expect(wrapper.find("[data-testid='message-command-toggle']").exists()).toBe(false);
  });

  it("shows a command without arguments by name", () => {
    const wrapper = bubble({ body: "Create or update AGENTS.md for this repo.", command: { name: "init" } });

    expect(wrapper.get("[data-testid='message-command']").text()).toBe("/init");
  });

  it("shows any other message as it is", () => {
    const wrapper = bubble({ body: "What does this repo do?" });

    expect(wrapper.find("[data-testid='message-command']").exists()).toBe(false);
    expect(wrapper.text()).toContain("What does this repo do?");
  });
});
