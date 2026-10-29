import type { WeaveFlavor, WeaveHarnessDetection } from "@/api/client";

export const WEAVE_CONFIG = "config.weave";
export const WEAVE_LEGACY_CONFIG = "weave-opencode.jsonc";
export const WEAVE_PROMPTS = "prompts/";

export const WEAVE_FLAVORS: Record<WeaveFlavor, {
  name: string;
  file: string;
  ownPath: string;
  variable: string;
  projectPath: string;
  minimum: string;
  starter: string;
}> = {
  weave: {
    name: "Weave",
    file: WEAVE_CONFIG,
    ownPath: "~/.weave/config.weave",
    variable: "WEAVE_GLOBAL_CONFIG_DIR",
    projectPath: "<repo>/.weave/config.weave",
    minimum: "0.2.0-next.1",
    starter: "# Fleet's Weave config. Weave reads it in place of ~/.weave/config.weave in Fleet sessions.\n"
      + "# A repo's own .weave/config.weave still applies on top.\n",
  },
  legacy: {
    name: "Weave Legacy",
    file: WEAVE_LEGACY_CONFIG,
    ownPath: "~/.config/opencode/weave-opencode.jsonc",
    variable: "WEAVE_OPENCODE_CONFIG_DIR",
    projectPath: "<repo>/.opencode/weave-opencode.jsonc",
    minimum: "0.9.0",
    starter: "{\n  // Fleet's Weave Legacy config. Weave Legacy reads it in place of\n"
      + "  // ~/.config/opencode/weave-opencode.jsonc in Fleet sessions.\n"
      + "  // A repo's own .opencode/weave-opencode.jsonc still applies on top.\n}\n",
  },
};

/** What a harness loads of Weave, in a line: its plugin entries, or why there are none. */
export function describeWeaveInstall(detection: WeaveHarnessDetection): string {
  if (!detection.checked) return detection.note ?? "";
  if (detection.installs.length === 0) {
    return detection.addTo
      ? `Weave isn't in its plugin list. Add Weave puts it in ${detection.addTo}.`
      : "Weave isn't in its plugin list.";
  }
  return detection.installs.map((install) => {
    const entry = install.entry.startsWith("file://")
      ? `${install.package} · local build at ${install.entry.slice("file://".length)}`
      : install.entry;
    return install.error ? `${entry} · didn't load: ${install.error}` : entry;
  }).join(" · ");
}

export function weavePill(detection: WeaveHarnessDetection): { tone: "ok" | "warn" | "off"; label: string } {
  if (!detection.checked) return { tone: "off", label: "Not yet" };
  if (detection.installs.length === 0) return { tone: "off", label: "No Weave" };
  const names = detection.installs.map((install) => WEAVE_FLAVORS[install.flavor].name).join(" + ");
  if (detection.installs.some((install) => install.error)) return { tone: "warn", label: `${names} · didn't load` };
  return detection.installs.every((install) => install.acceptsFleetConfig)
    ? { tone: "ok", label: names }
    : { tone: "warn", label: `${names} · update` };
}

/** Fleet can take Weave out of this harness: it's the entry Add Weave put in. */
export function weaveAddedByFleet(detection: WeaveHarnessDetection): boolean {
  return detection.installs.some((install) => install.addedByFleet);
}

/** The Weaves some harness loads, in the order they're shown. */
export function loadedFlavors(harnesses: readonly WeaveHarnessDetection[]): WeaveFlavor[] {
  const installs = harnesses.flatMap((harness) => harness.installs);
  return (["weave", "legacy"] as const).filter((flavor) => installs.some((install) => install.flavor === flavor));
}

/**
 * The Weaves some harness loads only at a version that can't read Fleet's folder. One that didn't load at all isn't
 * too old: why it didn't load is shown instead.
 */
export function outdatedFlavorsOf(harnesses: readonly WeaveHarnessDetection[]): WeaveFlavor[] {
  const loaded = harnesses.flatMap((harness) => harness.installs).filter((install) => !install.error);
  return (["weave", "legacy"] as const).filter((flavor) =>
    loaded.some((install) => install.flavor === flavor)
    && !loaded.some((install) => install.flavor === flavor && install.acceptsFleetConfig));
}

/** The Weaves Fleet can hand a config to: at least one harness loads a version that reads Fleet's folder. */
export function usableFlavorsOf(harnesses: readonly WeaveHarnessDetection[]): WeaveFlavor[] {
  const installs = harnesses.flatMap((harness) => harness.installs);
  return loadedFlavors(harnesses).filter((flavor) =>
    installs.some((install) => install.flavor === flavor && install.acceptsFleetConfig));
}
