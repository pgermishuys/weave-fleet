import { describe, it, expect, beforeEach } from "vitest";
import type { IntegrationManifest } from "../types";

// We need to test the registry in isolation, but since it uses module-level state,
// we re-import it fresh using dynamic import with cache busting.
// Instead, we test via the exported functions directly and reset state between tests.

// We can't easily reset module-level state with static imports, so we'll
// test behavior via the actual functions since they share state.
// Each test should not depend on order.

describe("Integration Registry", () => {
  let registry: typeof import("../registry");

  beforeEach(async () => {
    // Re-import the module fresh for each test by resetting via vi.resetModules
    const { vi } = await import("vitest");
    vi.resetModules();
    registry = await import("../registry");
  });

  function makeMockManifest(
    id: string,
    isConfigured = false
  ): IntegrationManifest {
    return {
      id,
      name: id.charAt(0).toUpperCase() + id.slice(1),
      icon: () => null,
      browserComponent: () => null,
      settingsComponent: () => null,
      isConfigured: () => isConfigured,
      resolveContext: async () => null,
    } as unknown as IntegrationManifest;
  }

  it("registers an integration and retrieves it by id", () => {
    const manifest = makeMockManifest("github");
    registry.registerIntegration(manifest);

    const found = registry.getIntegration("github");
    expect(found).toBe(manifest);
  });

  it("getIntegrations returns all registered integrations", () => {
    const a = makeMockManifest("github");
    const b = makeMockManifest("jira");
    registry.registerIntegration(a);
    registry.registerIntegration(b);

    const all = registry.getIntegrations();
    expect(all).toContain(a);
    expect(all).toContain(b);
  });

  it("getIntegration returns undefined for unknown id", () => {
    const result = registry.getIntegration("nonexistent");
    expect(result).toBeUndefined();
  });

  it("getConnectedIntegrations returns only configured integrations", () => {
    const connected = makeMockManifest("github", true);
    const disconnected = makeMockManifest("jira", false);
    registry.registerIntegration(connected);
    registry.registerIntegration(disconnected);

    const connectedList = registry.getConnectedIntegrations();
    expect(connectedList).toContain(connected);
    expect(connectedList).not.toContain(disconnected);
  });

  it("duplicate registration replaces the existing entry", () => {
    const original = makeMockManifest("github", false);
    const replacement = makeMockManifest("github", true);

    registry.registerIntegration(original);
    registry.registerIntegration(replacement);

    const all = registry.getIntegrations();
    const githubEntries = all.filter((m) => m.id === "github");
    expect(githubEntries).toHaveLength(1);
    expect(githubEntries[0]).toBe(replacement);
  });
});
