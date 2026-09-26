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

const harnessDisplayMetadata: Record<string, HarnessDisplayMetadata> = {
  opencode: {
    eyebrow: "CLI harness",
    description: "Harness for sessions backed by the OpenCode command-line runtime.",
    icon: TerminalSquare,
    pitch: "Open source. Includes free models, or sign in to your own provider.",
  },
  opencode2: {
    eyebrow: "CLI harness",
    description: "Harness for sessions backed by OpenCode 2's server, next to OpenCode 1 or on its own.",
    icon: TerminalSquare,
    pitch: "The new OpenCode. It can live in a folder of its own, next to OpenCode 1.",
  },
  "claude-code": {
    eyebrow: "CLI harness",
    description: "Harness for Anthropic Claude Code sessions and project-aware coding workflows.",
    icon: Hexagon,
    pitch: "Anthropic's coding tool. Needs a Claude subscription or an API key.",
  },
  pi: {
    eyebrow: "CLI harness",
    description: "Harness for sessions backed by the Pi command-line runtime from pi.dev.",
    icon: Infinity,
  },
};

const fallbackHarnessMetadata: HarnessDisplayMetadata = {
  eyebrow: "Harness",
  description: "Harness runtime registered by the backend.",
  icon: Cable,
};

export function harnessDisplay(type: string): HarnessDisplayMetadata {
  return harnessDisplayMetadata[type] ?? fallbackHarnessMetadata;
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
