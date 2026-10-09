import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, nextTick } from "vue";
import type { SessionListItem } from "@/api/client";

const { navigate, pathname } = vi.hoisted(() => ({
  navigate: vi.fn(),
  pathname: { value: "/" },
}));

vi.mock("@tanstack/vue-router", async () => {
  const { computed } = await import("vue");
  return {
    useRouter: () => ({ navigate }),
    useLocation: ({ select }: { select: (location: { pathname: string }) => string }) =>
      computed(() => select({ pathname: pathname.value })),
  };
});

vi.mock("@/api/client", () => ({ api: { GET: vi.fn(async () => ({ data: [], response: { ok: true } })) } }));

import { useCommands } from "@/composables/use-commands";
import { useCommandStore } from "@/stores/commands";
import { useKeybindingsStore } from "@/stores/keybindings";
import { useSessionsStore } from "@/stores/sessions";
import { useThemeStore } from "@/stores/theme";
import type { Command } from "@/lib/command-registry";

function item(id: string, title: string): SessionListItem {
  return {
    harnessType: "opencode",
    instanceId: `inst-${id}`, workspaceId: "w", workspaceDirectory: "/repo", workspaceDisplayName: null, isolationStrategy: "existing",
    sessionStatus: "idle", activityStatus: "idle", session: { id, title, time: { created: 1, updated: 1 }, tags: [] } as never,
    instanceStatus: "running", lifecycleStatus: "running", retentionStatus: "active", typedInstanceStatus: "running",
    isHidden: false, tags: [], projectId: null,
  } as unknown as SessionListItem;
}

/**
 * Listens for a session command the way the conversation does, and records what it was asked.
 * The plumbing lives here only, so the expectations below don't depend on how a command reaches the conversation.
 */
function listenFor(name: string): { calls: unknown[]; stop: () => void } {
  const calls: unknown[] = [];
  const handler = (event: Event) => calls.push((event as CustomEvent).detail);
  window.addEventListener(`weave:command-${name}`, handler);
  return { calls, stop: () => window.removeEventListener(`weave:command-${name}`, handler) };
}

function press(key: string, init: KeyboardEventInit = {}, target: EventTarget = document.body): KeyboardEvent {
  const event = new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true, ...init });
  target.dispatchEvent(event);
  return event;
}

describe("useCommands", () => {
  let wrapper: VueWrapper;
  let commands: ReturnType<typeof useCommandStore>;

  async function start() {
    wrapper = mount(defineComponent({ setup: () => { useCommands(); return () => null; } }), { attachTo: document.body });
    await nextTick();
  }

  const byId = (id: string): Command => {
    const command = commands.getCommand(id);
    if (!command) throw new Error(`no command ${id}`);
    return command;
  };

  beforeEach(() => {
    localStorage.clear();
    commands = useCommandStore();
    navigate.mockReset();
    pathname.value = "/";
    document.documentElement.style.fontSize = "";
  });

  afterEach(() => {
    wrapper?.unmount();
    document.body.innerHTML = "";
  });

  describe("what the palette lists", () => {
    it("lists every command by category (Session, Navigation, View, Fleet), then by label", async () => {
      await start();

      expect(commands.commands.map((command) => `${command.category}: ${command.label}`)).toEqual([
        "Session: Clear draft",
        "Session: Copy session ID",
        "Session: Export conversation",
        "Session: Focus prompt",
        "Session: Fork session",
        "Session: Go to file…",
        "Session: Go to session…",
        "Session: Interrupt session",
        "Session: New session",
        "Session: New session in this folder",
        "Session: Next session",
        "Session: Previous session",
        "Session: Refresh sessions",
        "Session: Scroll to bottom",
        "Session: Scroll to top",
        "Navigation: Go to Analytics",
        "Navigation: Go to Board",
        "Navigation: Go to Sessions",
        "Navigation: Go to Settings",
        "View: Cycle theme",
        "View: Hide right panel",
        "View: Hide sidebar",
        "View: Show all sessions",
        "View: Show inline tool diffs",
        "View: Show terminal",
        "View: Switch to dark mode",
        "View: Toggle full screen",
        "View: Zoom in",
        "View: Zoom out",
        "Fleet: Open marketplace panel",
        "Fleet: Report a problem",
      ]);
    });

    it("carries the keybinding's palette hotkey and global shortcut on the command", async () => {
      await start();

      expect(byId("new-session").paletteHotkey).toBe("n");
      expect(byId("new-session").globalShortcut).toEqual({ key: "n", platformModifier: true, metaKey: true });
      expect(byId("toggle-sidebar").globalShortcut).toEqual({ key: "b", platformModifier: true });
      expect(byId("nav-settings").paletteHotkey).toBe("s");
      expect(byId("nav-board").globalShortcut).toBeUndefined();
    });

    it("disables session commands until a session is open, and shows the one it can move to", async () => {
      useSessionsStore().setSessions([item("s1", "First"), item("s2", "Second")]);
      await start();

      expect(byId("focus-prompt").disabled).toBe(true);
      expect(byId("nav-next-session").disabled).toBe(true);

      useSessionsStore().setActiveSessionId("s1");
      await nextTick();

      expect(byId("focus-prompt").disabled).toBe(false);
      expect(byId("nav-next-session").disabled).toBe(false);
      expect(byId("nav-prev-session").disabled).toBe(false);
    });

    it("drills Go to session into one sub-command per session, the open one disabled", async () => {
      useSessionsStore().setSessions([item("s1", "First"), item("s2", "")]);
      useSessionsStore().setActiveSessionId("s1");
      await start();

      const parent = byId("nav-go-to-session");
      expect(parent.subCommands).toBeUndefined();
      const subs = parent.getSubCommands!();

      expect(subs.map((sub) => [sub.id, sub.label, sub.category, Boolean(sub.disabled)])).toEqual([
        ["nav-session-s1", "First", "Session", true],
        ["nav-session-s2", "s2", "Session", false],
      ]);

      subs[1]!.action();
      expect(useSessionsStore().activeSessionId).toBe("s2");
      expect(navigate).toHaveBeenCalledWith({
        to: "/sessions/$id",
        params: { id: "s2" },
        search: { instanceId: "inst-s2", parentSessionId: undefined },
      });
    });

    it("drops a command from the list when it goes away, and adds it back when it returns", async () => {
      useSessionsStore().setSessions([]);
      await start();
      expect(byId("nav-go-to-session").disabled).toBe(true);

      useSessionsStore().setSessions([item("s1", "First")]);
      await nextTick();
      expect(byId("nav-go-to-session").disabled).toBe(false);
    });

    it("removes everything it listed when it unmounts", async () => {
      await start();
      expect(commands.commands.length).toBeGreaterThan(20);

      wrapper.unmount();

      expect(commands.commands).toEqual([]);
    });
  });

  describe("what a keybinding triggers", () => {
    it("Ctrl K opens the palette, and closes it again, even while typing", async () => {
      await start();
      const field = document.createElement("textarea");
      document.body.append(field);

      expect(press("k", { ctrlKey: true }, field).defaultPrevented).toBe(true);
      expect(commands.paletteOpen).toBe(true);
      press("k", { ctrlKey: true }, field);
      expect(commands.paletteOpen).toBe(false);
    });

    it("runs the command whose shortcut matches, and stops the browser's own action", async () => {
      await start();

      const event = press("=", { ctrlKey: true });

      expect(event.defaultPrevented).toBe(true);
      expect(document.documentElement.style.fontSize).toBe("110%");
      press("-", { ctrlKey: true });
      press("-", { ctrlKey: true });
      expect(document.documentElement.style.fontSize).toBe("90%");
    });

    it("needs the exact modifiers", async () => {
      await start();

      press("=", {});
      press("=", { ctrlKey: true, shiftKey: true });
      press("=", { ctrlKey: true, altKey: true, key: "x" });

      expect(document.documentElement.style.fontSize).toBe("");
    });

    it("ignores a shortcut while typing in a field, unless the command allows it", async () => {
      await start();
      const field = document.createElement("input");
      document.body.append(field);

      press("=", { ctrlKey: true }, field);

      expect(document.documentElement.style.fontSize).toBe("");
    });

    it("skips a disabled command", async () => {
      useSessionsStore().setSessions([]);
      await start();
      const toggle = vi.spyOn(useThemeStore(), "setTheme");

      // Ctrl+Cmd+T cycles the theme; Ctrl+[ goes to the previous session, which can't while there is none.
      press("[", { ctrlKey: true });
      expect(navigate).not.toHaveBeenCalled();
      press("t", { ctrlKey: true, metaKey: true });
      expect(toggle).toHaveBeenCalledTimes(1);
    });

    it("moves to the next and previous session with Ctrl ] and Ctrl [", async () => {
      useSessionsStore().setSessions([item("s1", "First"), item("s2", "Second"), item("s3", "Third")]);
      useSessionsStore().setActiveSessionId("s1");
      await start();

      press("]", { ctrlKey: true });
      expect(useSessionsStore().activeSessionId).toBe("s2");
      useSessionsStore().setActiveSessionId("s1");
      await nextTick();
      press("[", { ctrlKey: true });
      expect(useSessionsStore().activeSessionId).toBe("s3");
    });

    it("follows a rebound shortcut, and forgets the old one", async () => {
      await start();

      useKeybindingsStore().updateBinding("zoom-in", { globalShortcut: { key: "y", platformModifier: true } });
      await nextTick();
      press("=", { ctrlKey: true });
      expect(document.documentElement.style.fontSize).toBe("");

      press("y", { ctrlKey: true });
      expect(document.documentElement.style.fontSize).toBe("110%");
    });

    it("Escape interrupts the open session, but not from inside a dialog", async () => {
      useSessionsStore().setSessions([item("s1", "First")]);
      useSessionsStore().setActiveSessionId("s1");
      await start();
      const dialog = document.createElement("div");
      dialog.setAttribute("data-slot", "dialog-content");
      const inside = document.createElement("button");
      dialog.append(inside);
      document.body.append(dialog);

      expect(press("Escape", {}, inside).defaultPrevented).toBe(false);
      expect(press("Escape").defaultPrevented).toBe(true);
    });

    it("leaves a key alone when something else already handled it", async () => {
      await start();
      const handled = new KeyboardEvent("keydown", { key: "=", ctrlKey: true, bubbles: true, cancelable: true });
      handled.preventDefault();

      document.body.dispatchEvent(handled);

      expect(document.documentElement.style.fontSize).toBe("");
    });
  });

  describe("what the session commands send to the conversation", () => {
    it("sends each one with the open session's id, and nothing while none is open", async () => {
      const heard = {
        "focus-prompt": listenFor("focus-prompt"),
        "copy-session-id": listenFor("copy-session-id"),
        "export-conversation": listenFor("export-conversation"),
        "scroll-top": listenFor("scroll-top"),
        "scroll-bottom": listenFor("scroll-bottom"),
      };
      useSessionsStore().setSessions([item("s1", "First")]);
      await start();

      byId("focus-prompt").action();
      byId("copy-session-id").action();
      byId("export-conversation").action();
      expect(Object.values(heard).map((entry) => entry.calls)).toEqual([[], [], [], [], []].map(() => []));

      useSessionsStore().setActiveSessionId("s1");
      byId("focus-prompt").action();
      byId("copy-session-id").action();
      byId("export-conversation").action();
      byId("scroll-to-top").action();
      byId("scroll-to-bottom").action();

      for (const [name, entry] of Object.entries(heard)) {
        expect(entry.calls, name).toEqual([{ sessionId: "s1" }]);
        entry.stop();
      }
    });

    it("scrolls even with no session open, naming none", async () => {
      const top = listenFor("scroll-top");
      await start();

      byId("scroll-to-top").action();

      expect(top.calls).toEqual([{ sessionId: null }]);
      top.stop();
    });

    it("sends a shortcut the same as the palette does", async () => {
      const focus = listenFor("focus-prompt");
      useSessionsStore().setSessions([item("s1", "First")]);
      useSessionsStore().setActiveSessionId("s1");
      await start();
      useKeybindingsStore().updateBinding("focus-prompt", { globalShortcut: { key: "y", platformModifier: true } });
      await nextTick();

      press("y", { ctrlKey: true });

      expect(focus.calls).toEqual([{ sessionId: "s1" }]);
      focus.stop();
    });
  });
});

