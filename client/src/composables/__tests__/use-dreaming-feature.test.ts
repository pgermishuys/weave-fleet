import { beforeEach, describe, expect, it, vi } from "vitest";
import { ref } from "vue";

const prefs = vi.hoisted(() => ({ values: {} as Record<string, string> }));

vi.mock("@/lib/machine-target", () => ({ useMachineTarget: () => ({}) }));
vi.mock("@/stores/preferences", () => ({
  usePreferencesStore: () => ({ ensureLoaded: vi.fn(), get: (key: string, fallback: string) => ref(prefs.values[key] ?? fallback).value }),
}));

const { useDreamingFeature } = await import("@/composables/use-dreaming-feature");

describe("useDreamingFeature", () => {
  beforeEach(() => { prefs.values = {}; });

  it("is off while Memory is off, whatever a leftover Dreaming preference says", () => {
    prefs.values = { Dreaming: "true" };
    expect(useDreamingFeature().isDreamingEnabled.value).toBe(false);
  });

  it("is on whenever Memory is on, with no Dreaming preference", () => {
    prefs.values = { Memory: "true" };
    expect(useDreamingFeature().isDreamingEnabled.value).toBe(true);
  });
});
