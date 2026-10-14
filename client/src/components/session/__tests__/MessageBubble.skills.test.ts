import { beforeEach, describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import MessageBubble from "@/components/session/MessageBubble.vue";
import { rememberSessionSkills, resetSessionSkills } from "@/lib/skill-mentions";
import { useSessionsStore } from "@/stores/sessions";
import type { SessionListItem } from "@/api/client";

function bubble(body: string, role: "user" | "assistant" = "user") {
  return mount(MessageBubble, {
    props: { author: "You", role, showIdentity: true, clusterPosition: "single", body, sessionId: "ses-cc" },
  });
}

describe("MessageBubble with $ skills", () => {
  beforeEach(() => {
    resetSessionSkills();
    rememberSessionSkills("ses-cc", [{ name: "fleet-debug", kind: "skill" }, { name: "fleet-simplify", kind: "skill" }]);
    useSessionsStore().sessions = [{ session: { id: "ses-cc" }, harnessType: "claude-code" } as unknown as SessionListItem];
  });

  it("shows each skill in your message as a chip, the one Claude Code got as a note in amber", () => {
    // The text as the harness's store gives it back after a reload: what was typed.
    const wrapper = bubble("The total is off by a cent. Use $fleet-debug to find it, then tidy with $fleet-simplify");

    const chips = wrapper.findAll(".skill-ref-chip");
    expect(chips.map((chip) => chip.text())).toEqual(["fleet-debug", "fleet-simplify"]);
    expect(chips.map((chip) => chip.classes("skill-ref-chip--note"))).toEqual([false, true]);
    expect(wrapper.text()).not.toContain("$fleet");
  });

  it("leaves unknown names, code and the agent's replies as text", () => {
    expect(bubble("Why is $HOME empty? `$fleet-debug`").find(".skill-ref-chip").exists()).toBe(false);
    expect(bubble("I ran $fleet-debug", "assistant").find(".skill-ref-chip").exists()).toBe(false);
  });
});
