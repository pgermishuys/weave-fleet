import { mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { SessionListItem } from "@/api/client";
import StatusBar from "@/components/layout/StatusBar.vue";
import type { Command } from "@/lib/command-registry";
import { useAppShellStore } from "@/stores/app-shell";
import { useCommandStore } from "@/stores/commands";
import { useSessionsStore } from "@/stores/sessions";
import { useTerminalsStore } from "@/stores/terminals";

/** Stands in for the command useCommands registers, which is what the keyboard shortcut runs too. */
function registerCommand(id: string, overrides: Partial<Command> = {}): Command {
  const command: Command = { id, label: id, category: "View", action: vi.fn(), ...overrides };
  useCommandStore().registerCommand(command);
  return command;
}

function openSession(activityStatus: string | null): void {
  const sessions = useSessionsStore();
  sessions.setSessions([
    { session: { id: "s1", title: "Session" }, instanceId: "i1", activityStatus, sessionStatus: "idle" } as unknown as SessionListItem,
  ]);
  sessions.setActiveSessionId("s1");
}

function enableTerminals(enabled: boolean): void {
  const appShell = useAppShellStore();
  appShell.setConfig({ ...appShell.config, terminalEnabled: enabled });
}

describe("StatusBar hints", () => {
  beforeEach(() => {
    globalThis.localStorage?.clear();
  });

  it("opens and closes the command palette the way Ctrl K does", async () => {
    const commands = useCommandStore();
    const wrapper = mount(StatusBar);
    const button = wrapper.get("[data-testid=status-hint-palette]");

    await button.trigger("click");
    expect(commands.paletteOpen).toBe(true);

    await button.trigger("click");
    expect(commands.paletteOpen).toBe(false);
  });

  it("runs the previous and next session commands from their own buttons", async () => {
    const previous = registerCommand("nav-prev-session", { label: "Previous session" });
    const next = registerCommand("nav-next-session", { label: "Next session" });
    const wrapper = mount(StatusBar);
    const previousButton = wrapper.get("[data-testid=status-hint-prev-session]");
    const nextButton = wrapper.get("[data-testid=status-hint-next-session]");

    expect(previousButton.attributes("title")).toContain("Previous session");
    expect(nextButton.attributes("title")).toContain("Next session");

    await previousButton.trigger("click");
    expect(previous.action).toHaveBeenCalledTimes(1);
    expect(next.action).not.toHaveBeenCalled();

    await nextButton.trigger("click");
    expect(next.action).toHaveBeenCalledTimes(1);
  });

  it("disables previous and next with the command's reason when there's no other session", () => {
    registerCommand("nav-prev-session", { disabled: true, description: "A second session is required." });
    registerCommand("nav-next-session", { disabled: true, description: "A second session is required." });
    const wrapper = mount(StatusBar);
    const previousButton = wrapper.get("[data-testid=status-hint-prev-session]");

    expect(previousButton.attributes("disabled")).toBeDefined();
    expect(previousButton.attributes("title")).toBe("A second session is required.");
  });

  it("runs the sidebar command and names what it will do", async () => {
    const sidebar = registerCommand("toggle-sidebar", { label: "Hide sidebar" });
    const wrapper = mount(StatusBar);
    const button = wrapper.get("[data-testid=status-hint-sidebar]");

    expect(button.attributes("aria-label")).toBe("Hide sidebar");
    await button.trigger("click");

    expect(sidebar.action).toHaveBeenCalledTimes(1);
  });

  it("interrupts only while the session is working", async () => {
    const interrupt = registerCommand("interrupt-session");
    openSession("idle");
    const wrapper = mount(StatusBar);
    const button = wrapper.get("[data-testid=status-hint-interrupt]");

    expect(button.attributes("disabled")).toBeDefined();
    expect(button.attributes("title")).toBe("Nothing to interrupt: the session isn't working");
    await button.trigger("click");
    expect(interrupt.action).not.toHaveBeenCalled();

    useSessionsStore().patchSession("s1", { activityStatus: "busy" });
    await wrapper.vm.$nextTick();

    expect(button.attributes("disabled")).toBeUndefined();
    await button.trigger("click");
    expect(interrupt.action).toHaveBeenCalledTimes(1);
  });

  it("keeps Interrupt in place, disabled, when no session is open", () => {
    registerCommand("interrupt-session", { disabled: true });
    const wrapper = mount(StatusBar);
    const button = wrapper.get("[data-testid=status-hint-interrupt]");

    expect(button.attributes("disabled")).toBeDefined();
    expect(button.attributes("title")).toBe("Open a session to interrupt it");
  });

  it("shows the terminal button only when terminals are on, and runs the terminal command", async () => {
    const terminal = registerCommand("toggle-terminal", { label: "Show terminal" });
    enableTerminals(false);
    const wrapper = mount(StatusBar);
    expect(wrapper.find("[data-testid=status-hint-terminal]").exists()).toBe(false);

    enableTerminals(true);
    await wrapper.vm.$nextTick();
    await wrapper.get("[data-testid=status-hint-terminal]").trigger("click");

    expect(terminal.action).toHaveBeenCalledTimes(1);
  });

  it("hides the terminal from the terminal's own hints, and leaves the rest as text", async () => {
    const terminal = registerCommand("toggle-terminal", { label: "Hide terminal" });
    useTerminalsStore().setFocused(true);
    const wrapper = mount(StatusBar);
    const hints = wrapper.get("[data-testid=terminal-keyboard-hint]");

    expect(hints.findAll("button")).toHaveLength(1);
    await hints.get("[data-testid=status-hint-hide-terminal]").trigger("click");

    expect(terminal.action).toHaveBeenCalledTimes(1);
  });

  it("doesn't take the keyboard when a hint is clicked", () => {
    registerCommand("interrupt-session");
    openSession("busy");
    const wrapper = mount(StatusBar);

    for (const button of wrapper.findAll("button")) {
      const press = new MouseEvent("mousedown", { bubbles: true, cancelable: true });
      button.element.dispatchEvent(press);
      expect(press.defaultPrevented, button.attributes("data-testid")).toBe(true);
    }
  });
});

describe("command store", () => {
  it("runs a command unless it's missing or disabled", () => {
    const commands = useCommandStore();
    const enabled = registerCommand("a");
    const disabled = registerCommand("b", { disabled: true });

    commands.runCommand("a");
    commands.runCommand("b");
    commands.runCommand("missing");

    expect(enabled.action).toHaveBeenCalledTimes(1);
    expect(disabled.action).not.toHaveBeenCalled();
  });
});
