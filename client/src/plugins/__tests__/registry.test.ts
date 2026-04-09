import { beforeEach, describe, expect, it, vi } from "vitest";
import type { FleetPluginManifest } from "@/plugins/types";

describe("plugin registry", () => {
  let registry: typeof import("@/plugins/registry");

  beforeEach(async () => {
    vi.resetModules();
    registry = await import("@/plugins/registry");
    registry.clearPlugins();
  });

  function makeManifest(id: string): FleetPluginManifest {
    return {
      descriptor: {
        id,
        displayName: id.toUpperCase(),
        trustLevel: "built-in",
        hasFrontend: true,
        hasBackend: false,
      },
    };
  }

  it("registers and retrieves a plugin by id", () => {
    const manifest = makeManifest("github");
    registry.registerPlugin(manifest);

    expect(registry.getPlugin("github")).toBe(manifest);
  });

  it("replaces duplicate registration", () => {
    const original = makeManifest("github");
    const replacement = makeManifest("github");
    registry.registerPlugin(original);
    registry.registerPlugin(replacement);

    expect(registry.getPlugins()).toHaveLength(1);
    expect(registry.getPlugin("github")).toBe(replacement);
  });
});
