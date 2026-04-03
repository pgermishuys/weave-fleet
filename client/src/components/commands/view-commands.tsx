"use client";

import { useEffect, useCallback } from "react";
import { PanelLeftClose, Palette, Filter, Maximize, ZoomIn, ZoomOut, Sun, Moon } from "lucide-react";
import { useCommandRegistry } from "@/contexts/command-registry-context";
import { useSidebar } from "@/contexts/sidebar-context";
import { useKeybindings } from "@/contexts/keybindings-context";
import { useTheme, ALL_THEMES, THEME_LABELS, type Theme } from "@/contexts/theme-context";

const THEME_CYCLE: Theme[] = ALL_THEMES;

export function ViewCommands() {
  const { registerCommand, unregisterCommand } = useCommandRegistry();
  const { toggleSidebar } = useSidebar();
  const { bindings } = useKeybindings();
  const { theme, setTheme } = useTheme();

  const toggle = useCallback(() => toggleSidebar(), [toggleSidebar]);

  const cycleTheme = useCallback(() => {
    const currentIndex = THEME_CYCLE.indexOf(theme);
    const nextIndex = (currentIndex + 1) % THEME_CYCLE.length;
    setTheme(THEME_CYCLE[nextIndex]);
  }, [theme, setTheme]);

  const toggleFullscreen = useCallback(() => {
    if (document.fullscreenElement) {
      document.exitFullscreen().catch(() => {});
    } else {
      document.documentElement.requestFullscreen().catch(() => {});
    }
  }, []);

  const zoomIn = useCallback(() => {
    const current = parseFloat(document.documentElement.style.fontSize || "100") || 100;
    document.documentElement.style.fontSize = `${Math.min(current + 10, 150)}%`;
  }, []);

  const zoomOut = useCallback(() => {
    const current = parseFloat(document.documentElement.style.fontSize || "100") || 100;
    document.documentElement.style.fontSize = `${Math.max(current - 10, 70)}%`;
  }, []);

  const toggleDarkLight = useCallback(() => {
    // Quick toggle between the two most common modes
    setTheme(theme === "light" ? "default" : "light");
  }, [theme, setTheme]);

  useEffect(() => {
    registerCommand({
      id: "toggle-sidebar",
      label: "Toggle Sidebar",
      icon: PanelLeftClose,
      category: "View",
      paletteHotkey: bindings["toggle-sidebar"]?.paletteHotkey ?? undefined,
      globalShortcut: bindings["toggle-sidebar"]?.globalShortcut ?? undefined,
      keywords: ["panel", "menu", "collapse", "expand"],
      action: toggle,
    });
    registerCommand({
      id: "cycle-theme",
      label: "Cycle Theme",
      description: `Current: ${theme}`,
      icon: Palette,
      category: "View",
      globalShortcut: bindings["cycle-theme"]?.globalShortcut ?? undefined,
      keywords: ["dark", "light", "black", "color", "appearance"],
      action: cycleTheme,
      getSubCommands: () =>
        THEME_CYCLE.map((t) => ({
          id: `theme-${t}`,
          label: `${THEME_LABELS[t]}${t === theme ? " (current)" : ""}`,
          icon: Palette,
          category: "View" as const,
          disabled: t === theme,
          action: () => setTheme(t),
        })),
    });
    registerCommand({
      id: "toggle-activity-filter",
      label: "Toggle Activity Filter",
      icon: Filter,
      category: "View",
      keywords: ["search", "find", "filter", "messages"],
      action: () => {
        document.dispatchEvent(
          new KeyboardEvent("keydown", {
            key: "f",
            metaKey: true,
            bubbles: true,
          })
        );
      },
    });
    registerCommand({
      id: "toggle-fullscreen",
      label: "Toggle Fullscreen",
      icon: Maximize,
      category: "View",
      keywords: ["maximize", "full", "screen", "expand"],
      action: toggleFullscreen,
    });
    registerCommand({
      id: "zoom-in",
      label: "Zoom In",
      icon: ZoomIn,
      category: "View",
      keywords: ["bigger", "larger", "increase", "font"],
      action: zoomIn,
    });
    registerCommand({
      id: "zoom-out",
      label: "Zoom Out",
      icon: ZoomOut,
      category: "View",
      keywords: ["smaller", "decrease", "font"],
      action: zoomOut,
    });
    registerCommand({
      id: "toggle-dark-light",
      label: "Toggle Dark / Light",
      description: `Current: ${theme === "light" ? "Light" : "Dark"}`,
      icon: theme === "light" ? Moon : Sun,
      category: "View",
      keywords: ["dark", "light", "mode", "switch"],
      action: toggleDarkLight,
    });

    return () => {
      unregisterCommand("toggle-sidebar");
      unregisterCommand("cycle-theme");
      unregisterCommand("toggle-activity-filter");
      unregisterCommand("toggle-fullscreen");
      unregisterCommand("zoom-in");
      unregisterCommand("zoom-out");
      unregisterCommand("toggle-dark-light");
    };
  }, [registerCommand, unregisterCommand, bindings, toggle, cycleTheme, theme, toggleFullscreen, zoomIn, zoomOut, toggleDarkLight]);

  return null;
}
