import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import ShellCommandBlock from "@/components/session/ShellCommandBlock.vue";
import type { ShellCommandView } from "@/lib/shell-commands";

function command(overrides: Partial<ShellCommandView> = {}): ShellCommandView {
  return { id: "part-1", command: "git status", output: "", state: "done", truncated: false, ...overrides };
}

describe("ShellCommandBlock", () => {
  it("shows the command as the user's, with its output", () => {
    const wrapper = mount(ShellCommandBlock, { props: { command: command({ output: "On branch main\n", exit: 0 }) } });

    expect(wrapper.text()).toContain("You ran");
    expect(wrapper.get("[data-testid='shell-command-text']").text()).toBe("$ git status");
    expect(wrapper.get("[data-testid='shell-command-output']").text()).toBe("On branch main");
    expect(wrapper.get("[data-testid='shell-command-state']").text()).toBe("exit 0");
  });

  it("marks a failed command with its exit code", () => {
    const wrapper = mount(ShellCommandBlock, { props: { command: command({ state: "failed", exit: 3, output: "boom\n" }) } });

    expect(wrapper.get("[data-testid='shell-command']").attributes("data-state")).toBe("failed");
    expect(wrapper.get("[data-testid='shell-command-state']").text()).toBe("Failed · exit 3");
  });

  it("says a running command is running, and that a finished one printed nothing", () => {
    const running = mount(ShellCommandBlock, { props: { command: command({ state: "running" }) } });
    expect(running.get("[data-testid='shell-command-state']").text()).toBe("Running…");
    expect(running.text()).not.toContain("No output");

    const quiet = mount(ShellCommandBlock, { props: { command: command() } });
    expect(quiet.text()).toContain("No output");
    expect(quiet.find("[data-testid='shell-command-state']").exists()).toBe(false);
  });

  it("collapses long output to its last lines until asked for the rest", async () => {
    const output = Array.from({ length: 40 }, (_, index) => `line ${index + 1}`).join("\n");
    const wrapper = mount(ShellCommandBlock, { props: { command: command({ output }) } });

    const shown = wrapper.get("[data-testid='shell-command-output']").text().split("\n");
    expect(shown).toHaveLength(12);
    expect(shown[0]).toBe("line 29");
    expect(shown.at(-1)).toBe("line 40");

    const expand = wrapper.get("[data-testid='shell-command-expand']");
    expect(expand.text()).toBe("Show 28 earlier lines");
    await expand.trigger("click");

    expect(wrapper.get("[data-testid='shell-command-output']").text().split("\n")).toHaveLength(40);
    expect(wrapper.find("[data-testid='shell-command-expand']").exists()).toBe(false);
  });

  it("says when the harness kept only part of the output", () => {
    const wrapper = mount(ShellCommandBlock, { props: { command: command({ output: "x\n", truncated: true }) } });

    expect(wrapper.text()).toContain("The harness kept only part of the output.");
  });
});
