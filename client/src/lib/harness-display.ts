import type { Component } from "vue";
import { AlertTriangle, Cable, CheckCircle2, CircleDashed, Hexagon, Infinity, TerminalSquare } from "lucide-vue-next";
import type { HarnessInfo, HarnessState } from "@/api/client";

/** How a harness shows in Settings and in setup. */
export interface HarnessDisplayMetadata {
  eyebrow: string;
  description: string;
  icon: Component;
  /** One line for choosing it during setup. */
  pitch?: string;
}

/** The icons a harness can ask for (`HarnessIcons` on the server). */
const harnessIcons: Record<string, Component> = {
  terminal: TerminalSquare,
  hexagon: Hexagon,
  infinity: Infinity,
  plug: Cable,
};

/** How the harness describes itself, with a plain stand-in for what it leaves out (a Fleet older than presentations). */
export function harnessDisplay(harness: Pick<HarnessInfo, "presentation">): HarnessDisplayMetadata {
  const presentation = harness.presentation;
  return {
    eyebrow: presentation?.eyebrow || "Harness",
    description: presentation?.description || "Harness runtime registered by the backend.",
    icon: (presentation?.icon && harnessIcons[presentation.icon]) || Cable,
    pitch: presentation?.pitch ?? undefined,
  };
}

/** The harness's name where room is short (the status bar), e.g. "Claude" for Claude Code. */
export function harnessShortName(harness: Pick<HarnessInfo, "displayName" | "presentation">): string {
  return harness.presentation?.shortName?.trim() || harness.displayName;
}

/** `text` split at backticks: the odd pieces are code. For the notes a harness sends, such as `profileNote`. */
export function splitCode(text: string): { text: string; code: boolean }[] {
  return text.split("`").map((piece, index) => ({ text: piece, code: index % 2 === 1 })).filter((piece) => piece.text !== "");
}

/** A harness's state, or "disabled" when the user turned it off. */
export type HarnessStatus = HarnessState | "disabled";

export function harnessState(harness: HarnessInfo): HarnessState {
  return harness.state ?? (harness.available ? "ready" : "not-working");
}

export function harnessStatusLabel(status: HarnessStatus): string {
  switch (status) {
    case "ready":
      return "Ready";
    case "not-installed":
      return "Not installed";
    case "sign-in-required":
      return "Sign-in needed";
    case "not-working":
      return "Not working";
    case "update-needed":
      return "Update needed";
    case "disabled":
      return "Disabled";
  }
}

export function harnessStatusClasses(status: HarnessStatus): string {
  switch (status) {
    case "ready":
      return "border-green-500/30 bg-green-500/10 text-green-300";
    case "sign-in-required":
    case "update-needed":
      return "border-yellow-500/30 bg-yellow-500/10 text-yellow-300";
    case "not-working":
      return "border-red-500/30 bg-red-500/10 text-red-300";
    case "not-installed":
    case "disabled":
      return "border-border bg-main-bg text-muted";
  }
}

export function harnessStatusIcon(status: HarnessStatus): Component {
  switch (status) {
    case "ready":
      return CheckCircle2;
    case "sign-in-required":
    case "not-working":
    case "update-needed":
      return AlertTriangle;
    case "not-installed":
    case "disabled":
      return CircleDashed;
  }
}

/** The version and path of the executable Fleet found, e.g. "1.18.30 · /home/you/.opencode/bin/opencode". */
export function harnessLocation(harness: HarnessInfo): string | null {
  const parts = [harness.version, harness.executablePath].filter((part): part is string => Boolean(part));
  return parts.length > 0 ? parts.join(" · ") : null;
}
