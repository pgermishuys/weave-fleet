import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Command } from "@/lib/command-registry";
import { useCommandStore } from "@/stores/commands";

/** The plumbing for putting a command in the palette lives here only, so the expectations don't depend on it. */
function register(store: ReturnType<typeof useCommandStore>, command: Partial<Command> & Pick<Command, "id" | "label" | "category">): Command {
  const full: Command = { action: vi.fn(), ...command };
  store.registerCommand(full);
  return full;
}

describe("command store", () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it("lists commands by category (Session, Navigation, View, Fleet), then by label", () => {
    const store = useCommandStore();
    register(store, { id: "report-problem", label: "Report a problem", category: "Fleet" });
    register(store, { id: "zoom-in", label: "Zoom in", category: "View" });
    register(store, { id: "nav-settings", label: "Go to Settings", category: "Navigation" });
    register(store, { id: "new-session", label: "New session", category: "Session" });
    register(store, { id: "cycle-theme", label: "Cycle theme", category: "View" });
    register(store, { id: "copy-session-id", label: "Copy session ID", category: "Session" });

    expect(store.commands.map((command) => command.id)).toEqual([
      "copy-session-id",
      "new-session",
      "nav-settings",
      "cycle-theme",
      "zoom-in",
      "report-problem",
    ]);
  });

  it("replaces a command registered again under the same id", () => {
    const store = useCommandStore();
    register(store, { id: "toggle-sidebar", label: "Hide sidebar", category: "View" });
    register(store, { id: "toggle-sidebar", label: "Show sidebar", category: "View" });

    expect(store.commands.map((command) => command.label)).toEqual(["Show sidebar"]);
    expect(store.getCommand("toggle-sidebar")?.label).toBe("Show sidebar");
  });

  it("forgets a command once it is unregistered", () => {
    const store = useCommandStore();
    register(store, { id: "zoom-in", label: "Zoom in", category: "View" });

    store.unregisterCommand("zoom-in");
    store.unregisterCommand("zoom-out");

    expect(store.commands).toEqual([]);
    expect(store.getCommand("zoom-in")).toBeUndefined();
  });

  it("keeps sub-commands on the command that owns them", () => {
    const store = useCommandStore();
    const child: Command = { id: "nav-session-s1", label: "First", category: "Session", action: vi.fn() };
    register(store, { id: "nav-go-to-session", label: "Go to session…", category: "Session", subCommands: [child] });
    register(store, { id: "nav-go-to-other", label: "Go to other…", category: "Session", getSubCommands: () => [child] });

    expect(store.commands.map((command) => command.id)).toEqual(["nav-go-to-other", "nav-go-to-session"]);
    expect(store.getCommand("nav-go-to-session")?.subCommands).toEqual([child]);
    expect(store.getCommand("nav-go-to-other")?.getSubCommands?.()).toEqual([child]);
  });

  it("runs a registered command, and does nothing for a missing or disabled one", () => {
    const store = useCommandStore();
    const enabled = register(store, { id: "zoom-in", label: "Zoom in", category: "View" });
    const disabled = register(store, { id: "zoom-out", label: "Zoom out", category: "View", disabled: true });

    store.runCommand("zoom-in");
    store.runCommand("zoom-out");
    store.runCommand("missing");

    expect(enabled.action).toHaveBeenCalledTimes(1);
    expect(disabled.action).not.toHaveBeenCalled();
  });

  it("opens, closes and toggles the palette", () => {
    const store = useCommandStore();

    store.togglePalette();
    expect(store.paletteOpen).toBe(true);
    store.setPaletteOpen(false);
    expect(store.paletteOpen).toBe(false);
    store.togglePalette();
    store.togglePalette();
    expect(store.paletteOpen).toBe(false);
  });

  it("remembers the five commands used last, newest first, and keeps them across a reload", () => {
    const store = useCommandStore();

    for (const id of ["a", "b", "c", "d", "e", "f", "c"]) store.recordUsage(id);

    expect(store.recentIds).toEqual(["c", "f", "e", "d", "b"]);
    expect(JSON.parse(localStorage.getItem("weave-fleet-vue-ui.command-recent-ids")!)).toEqual(["c", "f", "e", "d", "b"]);
  });
});
