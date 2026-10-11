import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { reactive } from "vue";
import type { KeptMod, ModsView } from "@/lib/mods/kept";

const router = vi.hoisted(() => ({ navigate: vi.fn() }));
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => router }));

const store = vi.hoisted(() => ({
  state: null as unknown as {
    kept: unknown;
    safeMode: boolean;
    isSwitchedOn: boolean;
    loadSwitch: ReturnType<typeof vi.fn>;
    loadKept: ReturnType<typeof vi.fn>;
    setOn: ReturnType<typeof vi.fn>;
    undo: ReturnType<typeof vi.fn>;
    setSafeMode: ReturnType<typeof vi.fn>;
  },
}));
vi.mock("@/stores/mods", () => ({ useModsStore: () => store.state }));

const { default: ModsSection } = await import("@/components/settings/ModsSection.vue");
const { useSettingsNav } = await import("@/composables/use-settings-nav");

enableAutoUnmount(afterEach);

function version(number: number, overrides: Record<string, unknown> = {}) {
  return {
    number,
    createdAt: "2026-10-08T09:00:00Z",
    version: `0.${number}.0`,
    sha256: "x",
    sessionId: `s-${number}`,
    sessionTitle: "Make test output readable",
    note: null,
    ...overrides,
  };
}

const chips: KeptMod = {
  name: "test-chips",
  description: "Shows test results as chips",
  active: 2,
  activeVersion: "0.2.0",
  off: null,
  versions: [version(1), version(2, { note: "Chips wrap now" })],
};
const gauge: KeptMod = {
  name: "context-gauge",
  description: "A gauge of context use",
  active: 1,
  activeVersion: "0.1.0",
  off: { by: "user", at: "2026-10-09T09:00:00Z" },
  versions: [version(1, { sessionId: null, sessionTitle: null })],
};
const broken: KeptMod = {
  name: "branch-badge",
  description: "A badge for the branch",
  active: 3,
  activeVersion: "0.3.0",
  off: { by: "strikes", at: "2026-10-09T09:00:00Z", error: "Cannot read x of undefined" },
  versions: [version(1), version(2), version(3)],
};

function setKept(mods: KeptMod[], safeMode = false) {
  store.state.kept = { safeMode, mods } satisfies ModsView;
  store.state.safeMode = safeMode;
}

async function mountSection() {
  const wrapper = mount(ModsSection);
  await flushPromises();
  return wrapper;
}

beforeEach(() => {
  vi.clearAllMocks();
  store.state = reactive({
    kept: null,
    safeMode: false,
    isSwitchedOn: true,
    loadSwitch: vi.fn().mockResolvedValue(undefined),
    loadKept: vi.fn().mockResolvedValue(undefined),
    setOn: vi.fn().mockResolvedValue(undefined),
    undo: vi.fn().mockResolvedValue(undefined),
    setSafeMode: vi.fn().mockResolvedValue(undefined),
  });
  setKept([chips, gauge, broken]);
});

describe("ModsSection", () => {
  it("lists every kept mod with version, description, kept date and the session it came from", async () => {
    const wrapper = await mountSection();
    expect(store.state.loadKept).toHaveBeenCalled();
    const row = wrapper.get("[data-testid=mod-row-test-chips]");
    expect(row.text()).toContain("test-chips");
    expect(row.text()).toContain("v2");
    expect(row.text()).toContain("0.2.0");
    expect(row.text()).toContain("Shows test results as chips");
    expect(row.text()).toContain("kept");
    expect(row.text()).toContain("Make test output readable");
    expect(row.get("a").attributes("href")).toBe("/sessions/s-2");
    await row.get("a").trigger("click");
    expect(router.navigate).toHaveBeenCalledWith(expect.objectContaining({ params: { id: "s-2" } }));
    expect(wrapper.get("[data-testid=mod-row-context-gauge]").find("a").exists()).toBe(false);
  });

  it("shows loading, then the error with the server's words and a Retry", async () => {
    store.state.kept = null;
    let reject!: (error: Error) => void;
    store.state.loadKept.mockReturnValueOnce(new Promise((_, r) => (reject = r)));
    const wrapper = mount(ModsSection);
    await flushPromises();
    expect(wrapper.find("[data-testid=mods-loading]").exists()).toBe(true);
    reject(new Error("Mods are off."));
    await flushPromises();
    expect(wrapper.get("[data-testid=mods-error]").text()).toContain("Mods are off.");
    await wrapper.get("[data-testid=mods-retry]").trigger("click");
    expect(store.state.loadKept).toHaveBeenCalledTimes(2);
  });

  it("says so when nothing is kept yet", async () => {
    setKept([]);
    const wrapper = await mountSection();
    expect(wrapper.get("[data-testid=mods-empty]").text()).toContain("No kept mods yet. When an agent writes a mod in a session, you can keep it from there.");
  });

  it("shows the safe-mode callout and turns mods back on", async () => {
    setKept([chips], true);
    const wrapper = await mountSection();
    expect(wrapper.get("[data-testid=mods-safe-mode]").text()).toContain("Mods are stopped for now (Start without mods).");
    await wrapper.get("[data-testid=mods-safe-mode-off]").trigger("click");
    expect(store.state.setSafeMode).toHaveBeenCalledWith(false);
  });

  it("links to the Features section, where the switch and the runtime are", async () => {
    const { activeSection } = useSettingsNav();
    activeSection.value = "mods";
    const wrapper = await mountSection();
    expect(wrapper.text()).toContain("The switch and the mod runtime (Bun) are in Features.");
    await wrapper.get("[data-testid=mods-features-link]").trigger("click");
    expect(activeSection.value).toBe("features");
  });

  it("points at Features instead when the Mods switch is off", async () => {
    store.state.isSwitchedOn = false;
    const wrapper = await mountSection();
    expect(wrapper.get("[data-testid=mods-switched-off]").text()).toContain("Turn Mods on in Settings → Features");
    expect(wrapper.find("[data-testid^=mod-row-]").exists()).toBe(false);
  });

  describe("on and off", () => {
    it("reflects each mod's state in its switch and flips it", async () => {
      const wrapper = await mountSection();
      expect(wrapper.get("[data-testid=mod-switch-test-chips]").attributes("aria-checked")).toBe("true");
      expect(wrapper.get("[data-testid=mod-switch-context-gauge]").attributes("aria-checked")).toBe("false");
      expect(wrapper.get("[data-testid=mod-row-context-gauge]").text()).toContain("Off");
      await wrapper.get("[data-testid=mod-switch-test-chips]").trigger("click");
      expect(store.state.setOn).toHaveBeenCalledWith("test-chips", false);
    });

    it("says why a mod turned itself off and turns it on again", async () => {
      const wrapper = await mountSection();
      const row = wrapper.get("[data-testid=mod-row-branch-badge]");
      expect(row.text()).toContain("Turned off after 3 failures: Cannot read x of undefined");
      await row.get("[data-testid=mod-turn-on-branch-badge]").trigger("click");
      expect(store.state.setOn).toHaveBeenCalledWith("branch-badge", true);
      expect(wrapper.get("[data-testid=mod-row-context-gauge]").find("[data-testid=mod-turn-on-context-gauge]").exists()).toBe(false);
    });
  });

  describe("Undo", () => {
    it("says where it goes back to", async () => {
      const wrapper = await mountSection();
      expect(wrapper.get("[data-testid=mod-undo-test-chips]").text()).toBe("Undo (back to v1)");
      await wrapper.get("[data-testid=mod-undo-test-chips]").trigger("click");
      expect(store.state.undo).toHaveBeenCalledWith("test-chips");
    });

    it("is disabled, with the reason, on v1 when already off", async () => {
      const wrapper = await mountSection();
      const button = wrapper.get("[data-testid=mod-undo-context-gauge]");
      expect(button.attributes("disabled")).toBeDefined();
      expect(button.attributes("title")).toBe("Nothing to undo: v1 is the first version and the mod is already off.");
    });

    it("shows the server's refusal", async () => {
      store.state.undo.mockRejectedValueOnce(new Error("Nothing to undo."));
      const wrapper = await mountSection();
      await wrapper.get("[data-testid=mod-undo-test-chips]").trigger("click");
      await flushPromises();
      expect(wrapper.get("[data-testid=mod-error-test-chips]").text()).toContain("Nothing to undo.");
    });
  });
});
