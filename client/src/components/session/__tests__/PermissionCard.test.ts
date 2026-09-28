import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import PermissionCard from "@/components/session/PermissionCard.vue";
import type { PermissionAsk } from "@/composables/use-session-permissions";

const { navigateMock } = vi.hoisted(() => ({ navigateMock: vi.fn() }));

vi.mock("@tanstack/vue-router", () => ({ useNavigate: () => navigateMock }));

function ask(overrides: Partial<PermissionAsk> = {}): PermissionAsk {
  return {
    id: "per_1",
    sessionId: "s1",
    kind: "shell",
    tool: "bash",
    title: "git push origin main",
    always: ["git push *"],
    askedAt: "2026-09-28T10:00:00Z",
    ...overrides,
  };
}

function mountCard(value: PermissionAsk, onAnswer = vi.fn(async () => {})) {
  const wrapper = mount(PermissionCard, { props: { ask: value, onAnswer }, attachTo: document.body });
  return { wrapper, onAnswer };
}

describe("PermissionCard", () => {
  it("shows the command and that the session needs you", () => {
    const { wrapper } = mountCard(ask({ directory: "~/source/weave-fleet" }));
    const card = wrapper.get("[data-testid='permission-card']");

    expect(card.text()).toContain("Run a command");
    expect(card.text()).toContain("git push origin main");
    expect(card.text()).toContain("~/source/weave-fleet");
    expect(card.text()).toContain("Needs you");
    expect(wrapper.get("[data-testid='permission-allow-always']").text()).toContain("git push *");
    wrapper.unmount();
  });

  it("shows an edit's diff without the file headers, with what it adds and removes", () => {
    const detail = "Index: a.ts\n===\n--- a.ts\n+++ a.ts\n@@ -1 +1 @@\n-let a = 1;\n+const a = 1;\n+export { a };";
    const { wrapper } = mountCard(ask({ kind: "edit", tool: "edit", title: "src/a.ts", detail, always: ["*"] }));

    const lines = wrapper.findAll(".pcard__line").map((line) => line.text());
    expect(lines).toEqual(["@@ -1 +1 @@", "-let a = 1;", "+const a = 1;", "+export { a };"]);
    expect(wrapper.get(".pcard__add").text()).toBe("+2");
    expect(wrapper.get(".pcard__del").text()).toBe("−1");
    expect(wrapper.get("[data-testid='permission-allow-always']").text()).toContain("Don't ask again for file edits");
    wrapper.unmount();
  });

  it("shows no lines for a write that changes nothing", () => {
    const { wrapper } = mountCard(ask({ kind: "edit", tool: "write", title: "NOTES.md", detail: "Index: NOTES.md\n===\n--- NOTES.md\n+++ NOTES.md\n", always: ["*"] }));

    expect(wrapper.find(".pcard__lines").exists()).toBe(false);
    expect(wrapper.get(".pcard__path").text()).toBe("NOTES.md");
    wrapper.unmount();
  });

  it("says when a subagent asked", () => {
    const { wrapper } = mountCard(ask({ subagent: "subagent" }));

    expect(wrapper.get("[data-testid='permission-card']").text()).toContain("Subagent");
    wrapper.unmount();
  });

  it("answers once, or for the rest of the session", async () => {
    const { wrapper, onAnswer } = mountCard(ask());

    await wrapper.get("[data-testid='permission-allow-once']").trigger("click");
    await wrapper.get("[data-testid='permission-allow-always']").trigger("click");

    expect(onAnswer).toHaveBeenNthCalledWith(1, "once", undefined);
    expect(onAnswer).toHaveBeenNthCalledWith(2, "always", undefined);
    wrapper.unmount();
  });

  it("answers by number key, and denies with Escape", async () => {
    const { wrapper, onAnswer } = mountCard(ask());
    const card = wrapper.get("[data-testid='permission-card']");

    await card.trigger("keydown", { key: "2" });
    await card.trigger("keydown", { key: "Escape" });

    expect(onAnswer).toHaveBeenNthCalledWith(1, "always", undefined);
    expect(onAnswer).toHaveBeenNthCalledWith(2, "reject", "");
    wrapper.unmount();
  });

  it("denies with the words typed for the agent on Enter", async () => {
    const { wrapper, onAnswer } = mountCard(ask());
    const input = wrapper.get("[data-testid='permission-deny-input']");

    await input.setValue("Push after the review");
    await input.trigger("keydown", { key: "Enter" });

    expect(onAnswer).toHaveBeenCalledWith("reject", "Push after the review");
    wrapper.unmount();
  });

  it("keeps the card and says why when the answer fails", async () => {
    const { wrapper } = mountCard(ask(), vi.fn(async () => {
      throw new Error("The harness is restarting.");
    }));

    await wrapper.get("[data-testid='permission-allow-once']").trigger("click");
    await vi.waitFor(() => expect(wrapper.find("[role='alert']").exists()).toBe(true));

    expect(wrapper.get("[role='alert']").text()).toBe("The harness is restarting.");
    wrapper.unmount();
  });

  it("opens Permission settings", async () => {
    const { wrapper } = mountCard(ask());

    await wrapper.get(".pcard__settings").trigger("click");

    expect(navigateMock).toHaveBeenCalledWith({ to: "/settings" });
    const { useSettingsNav } = await import("@/composables/use-settings-nav");
    expect(useSettingsNav().activeSection.value).toBe("permissions");
    wrapper.unmount();
  });
});
