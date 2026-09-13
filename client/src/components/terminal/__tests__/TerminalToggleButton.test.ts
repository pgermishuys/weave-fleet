import { mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it } from "vitest";
import TerminalToggleButton from "@/components/terminal/TerminalToggleButton.vue";
import { useAppShellStore } from "@/stores/app-shell";
import { useTerminalsStore } from "@/stores/terminals";

function enableTerminals(enabled: boolean): void {
  const appShell = useAppShellStore();
  appShell.setConfig({ ...appShell.config, terminalEnabled: enabled });
}

describe("TerminalToggleButton", () => {
  beforeEach(() => {
    // The drawer's open state is remembered in localStorage (missing on Node 26, hence ?.).
    globalThis.localStorage?.clear();
  });

  it("isn't there when Fleet has terminals turned off", () => {
    enableTerminals(false);

    const wrapper = mount(TerminalToggleButton, { props: { sessionId: "s1" } });

    expect(wrapper.find("[data-testid=terminal-toggle]").exists()).toBe(false);
  });

  it("shows and hides the session's drawer", async () => {
    enableTerminals(true);
    const store = useTerminalsStore();
    const wrapper = mount(TerminalToggleButton, { props: { sessionId: "s1" } });
    const button = wrapper.get("[data-testid=terminal-toggle]");

    expect(button.attributes("aria-pressed")).toBe("false");
    await button.trigger("click");

    expect(store.isOpen("s1")).toBe(true);
    expect(button.attributes("aria-pressed")).toBe("true");
    expect(button.attributes("title")).toContain("Hide terminal");
  });

  it("marks shells still running while the drawer is hidden", async () => {
    enableTerminals(true);
    const store = useTerminalsStore();
    store.setTerminals("s1", [{ id: "t1", title: "zsh", status: "running", createdAt: "2026-09-13T10:00:00Z" }]);
    const wrapper = mount(TerminalToggleButton, { props: { sessionId: "s1" } });

    expect(wrapper.find(".terminal-toggle__dot").exists()).toBe(true);
    expect(wrapper.get("[data-testid=terminal-toggle]").attributes("aria-label")).toContain("shells are running");

    store.setOpen("s1", true);
    await wrapper.vm.$nextTick();
    expect(wrapper.find(".terminal-toggle__dot").exists()).toBe(false);
  });
});
