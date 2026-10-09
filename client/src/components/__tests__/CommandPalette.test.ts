import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { CommandId } from "@/lib/command-ids";
import CommandPalette from "@/components/CommandPalette.vue";
import { commandPoint, type Command } from "@/lib/command-registry";
import { useCommandStore } from "@/stores/commands";

function command(id: CommandId, label: string, category: Command["category"], extra: Partial<Command> = {}): Command {
  return { id, label, category, action: vi.fn(), ...extra };
}

/** The plumbing for putting commands in the palette lives here only, so the expectations don't depend on it. */
function register(...commands: Command[]): void {
  commandPoint.contribute("test", commands);
}

const text = () => document.body.textContent ?? "";
const items = () => Array.from(document.querySelectorAll("[data-slot='command-item']"));
const labels = () => items().map((node) => node.querySelector("span.flex-1")?.textContent?.trim());
const click = (node: Element) => node.dispatchEvent(new MouseEvent("click", { bubbles: true }));

describe("CommandPalette", () => {
  let wrapper: VueWrapper;

  async function open() {
    wrapper = mount(CommandPalette, { attachTo: document.body, global: { stubs: { teleport: false } } });
    useCommandStore().setPaletteOpen(true);
    await flushPromises();
    await flushPromises();
  }

  beforeEach(() => {
    localStorage.clear();
    commandPoint.clear();
    vi.stubGlobal("ResizeObserver", class { observe() {} unobserve() {} disconnect() {} });
    Element.prototype.scrollIntoView = vi.fn();
  });

  afterEach(() => {
    wrapper?.unmount();
    vi.unstubAllGlobals();
  });

  it("shows nothing until the palette is open", async () => {
    register(command("zoom-in", "Zoom in", "View"));
    wrapper = mount(CommandPalette, { attachTo: document.body, global: { stubs: { teleport: false } } });
    await flushPromises();

    expect(text()).not.toContain("Zoom in");
  });

  it("groups the commands under Session, Navigation, View and Fleet, leaving out the empty groups", async () => {
    register(
      command("report-problem", "Report a problem", "Fleet"),
      command("zoom-in", "Zoom in", "View"),
      command("new-session", "New session", "Session"),
      command("cycle-theme", "Cycle theme", "View"),
    );
    await open();

    expect(labels()).toEqual(["New session", "Cycle theme", "Zoom in", "Report a problem"]);
    const heading = (name: string) => text().indexOf(`${name}${name === "Session" ? "New" : name === "View" ? "Cycle" : "Report"}`);
    expect([heading("Session"), heading("View"), heading("Fleet")]).toEqual(
      [heading("Session"), heading("View"), heading("Fleet")].sort((a, b) => a - b),
    );
    expect(heading("Session")).toBeGreaterThanOrEqual(0);
    expect(text()).not.toContain("Navigation");
  });

  it("shows the description, and the shortcut when there is one", async () => {
    register(
      command("new-session", "New session", "Session", {
        description: "Create and open a new session.",
        globalShortcut: { key: "n", platformModifier: true },
      }),
    );
    await open();

    expect(text()).toContain("Create and open a new session.");
    expect(document.querySelector("kbd")?.textContent?.trim()).toBe("Ctrl+N");
  });

  it("runs the chosen command, remembers it and closes", async () => {
    const zoom = command("zoom-in", "Zoom in", "View");
    register(zoom);
    await open();

    click(items()[0]!);
    await flushPromises();

    expect(zoom.action).toHaveBeenCalledTimes(1);
    expect(useCommandStore().recentIds).toEqual(["zoom-in"]);
    expect(useCommandStore().paletteOpen).toBe(false);
  });

  it("does not run a disabled command", async () => {
    const fork = command("fork-session", "Fork session", "Session", { disabled: true });
    register(fork);
    await open();

    click(items()[0]!);
    await flushPromises();

    expect(fork.action).not.toHaveBeenCalled();
    expect(useCommandStore().paletteOpen).toBe(true);
  });

  it("drills into sub-commands, with a Back row, and runs one of them", async () => {
    const child = command("nav-session-s1", "Rename the helper", "Session");
    register(
      command("nav-go-to-session", "Go to session…", "Session", { getSubCommands: () => [child] }),
      command("zoom-in", "Zoom in", "View"),
    );
    await open();

    click(items().find((node) => node.textContent?.includes("Go to session"))!);
    await flushPromises();

    expect(useCommandStore().paletteOpen).toBe(true);
    expect(text()).toContain("Back");
    expect(text()).toContain("Rename the helper");
    expect(text()).not.toContain("Zoom in");

    click(items().find((node) => node.textContent?.includes("Rename the helper"))!);
    await flushPromises();

    expect(child.action).toHaveBeenCalledTimes(1);
    expect(useCommandStore().recentIds).toEqual(["nav-session-s1"]);
  });
});
