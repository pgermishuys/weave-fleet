import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h, reactive } from "vue";
import type { KeptMod, ModsView } from "@/lib/mods/kept";

const files = vi.hoisted(() => ({ fetchModVersionFiles: vi.fn() }));
vi.mock("@/lib/mods/kept-api", () => files);

const log = vi.hoisted(() => ({ fetchModLog: vi.fn() }));
vi.mock("@/lib/mods/mod-log", () => log);

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
    activateVersion: ReturnType<typeof vi.fn>;
    setSafeMode: ReturnType<typeof vi.fn>;
  },
}));
vi.mock("@/stores/mods", () => ({ useModsStore: () => store.state }));

const { default: ModsSection } = await import("@/components/settings/ModsSection.vue");
const { useSettingsNav } = await import("@/composables/use-settings-nav");

enableAutoUnmount(afterEach);

const CodeDialogStub = defineComponent({
  name: "ModCodeDialog",
  props: { open: Boolean, title: String, load: Function },
  emits: ["update:open"],
  render() {
    return h("div", { "data-testid": "code-dialog", "data-open": String(this.open) }, this.title);
  },
});

function version(number: number, overrides: Record<string, unknown> = {}) {
  return {
    number,
    createdAt: "2026-10-08T09:00:00Z",
    version: `0.${number}.0`,
    sha256: "x",
    sessionId: `s-${number}`,
    sessionTitle: "Make test output readable",
    note: null,
    check: null,
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
  const wrapper = mount(ModsSection, { global: { stubs: { ModCodeDialog: CodeDialogStub } } });
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
    activateVersion: vi.fn().mockResolvedValue(undefined),
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
    expect(router.navigate).toHaveBeenCalledWith(expect.objectContaining({ to: "/sessions/$id", params: { id: "s-2" } }));
    expect(wrapper.findAll("[data-testid^=mod-row-]")).toHaveLength(3);
    expect(wrapper.get("[data-testid=mod-row-context-gauge]").find("a").exists()).toBe(false);
  });

  it("shows loading, then the error with the server's words and a Retry", async () => {
    store.state.kept = null;
    let reject!: (error: Error) => void;
    store.state.loadKept.mockReturnValueOnce(new Promise((_, r) => (reject = r)));
    const wrapper = mount(ModsSection, { global: { stubs: { ModCodeDialog: CodeDialogStub } } });
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

  it("has no safe-mode callout otherwise", async () => {
    const wrapper = await mountSection();
    expect(wrapper.find("[data-testid=mods-safe-mode]").exists()).toBe(false);
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
    it("reflects each mod's state in its switch", async () => {
      const wrapper = await mountSection();
      expect(wrapper.get("[data-testid=mod-switch-test-chips]").attributes("aria-checked")).toBe("true");
      expect(wrapper.get("[data-testid=mod-switch-context-gauge]").attributes("aria-checked")).toBe("false");
      expect(wrapper.get("[data-testid=mod-row-context-gauge]").text()).toContain("Off");
    });

    it("calls setOn when flipped", async () => {
      const wrapper = await mountSection();
      await wrapper.get("[data-testid=mod-switch-test-chips]").trigger("click");
      expect(store.state.setOn).toHaveBeenCalledWith("test-chips", false);
      await wrapper.get("[data-testid=mod-switch-context-gauge]").trigger("click");
      expect(store.state.setOn).toHaveBeenCalledWith("context-gauge", true);
    });

    it("disables the switch while saving and shows a failure inline", async () => {
      let reject!: (error: Error) => void;
      store.state.setOn.mockReturnValueOnce(new Promise((_, r) => (reject = r)));
      const wrapper = await mountSection();
      await wrapper.get("[data-testid=mod-switch-test-chips]").trigger("click");
      expect(wrapper.get("[data-testid=mod-switch-test-chips]").attributes("disabled")).toBeDefined();
      reject(new Error("That mod isn't kept."));
      await flushPromises();
      expect(wrapper.get("[data-testid=mod-error-test-chips]").text()).toContain("That mod isn't kept.");
      expect(wrapper.get("[data-testid=mod-switch-test-chips]").attributes("disabled")).toBeUndefined();
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
      expect(wrapper.get("[data-testid=mod-undo-test-chips]").attributes("disabled")).toBeUndefined();
    });

    it("on v1 while on, turns the mod off", async () => {
      setKept([{ ...chips, active: 1, activeVersion: "0.1.0", versions: [version(1)] }]);
      const wrapper = await mountSection();
      expect(wrapper.get("[data-testid=mod-undo-test-chips]").text()).toBe("Undo (turn off)");
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

  describe("history", () => {
    async function openHistory(name = "branch-badge") {
      const wrapper = await mountSection();
      await wrapper.get(`[data-testid=mod-history-toggle-${name}]`).trigger("click");
      return wrapper;
    }

    it("lists versions newest first with the Active badge, notes and sessions", async () => {
      const wrapper = await openHistory("test-chips");
      const rows = wrapper.findAll("[data-testid^=mod-version-test-chips-]");
      expect(rows.map((r) => r.attributes("data-testid"))).toEqual(["mod-version-test-chips-2", "mod-version-test-chips-1"]);
      expect(rows[0].text()).toContain("Active");
      expect(rows[0].text()).toContain("0.2.0");
      expect(rows[0].text()).toContain("Chips wrap now");
      expect(rows[0].text()).toContain("Make test output readable");
      expect(rows[1].text()).not.toContain("Active");
    });

    it("uses another version", async () => {
      const wrapper = await openHistory();
      expect(wrapper.find("[data-testid=mod-use-branch-badge-3]").exists()).toBe(false);
      await wrapper.get("[data-testid=mod-use-branch-badge-1]").trigger("click");
      expect(store.state.activateVersion).toHaveBeenCalledWith("branch-badge", 1);
    });

    it("shows the code of a version in the dialog, loading it by name and number", async () => {
      files.fetchModVersionFiles.mockResolvedValue([{ path: "index.ts", content: "x" }]);
      const wrapper = await openHistory();
      expect(wrapper.findComponent(CodeDialogStub).props("open")).toBe(false);
      await wrapper.get("[data-testid=mod-code-branch-badge-2]").trigger("click");
      const dialog = wrapper.findComponent(CodeDialogStub);
      expect(dialog.props("open")).toBe(true);
      expect(dialog.props("title")).toBe("branch-badge · v2");
      await (dialog.props("load") as () => Promise<unknown>)();
      expect(files.fetchModVersionFiles).toHaveBeenCalledWith("branch-badge", 2);
    });
  });

  describe("log", () => {
    async function openLog(name = "test-chips") {
      const wrapper = await mountSection();
      const details = wrapper.get(`[data-testid=mod-log-${name}]`);
      (details.element as HTMLDetailsElement).open = true;
      await details.trigger("toggle");
      await flushPromises();
      return wrapper;
    }

    it("is collapsed and fetches nothing until opened", async () => {
      const wrapper = await mountSection();
      expect(wrapper.get("[data-testid=mod-log-test-chips]").attributes("open")).toBeUndefined();
      expect(log.fetchModLog).not.toHaveBeenCalled();
    });

    it("fetches once when expanded and shows the lines with their levels", async () => {
      log.fetchModLog.mockResolvedValue([
        { at: "2026-10-09T09:00:00Z", level: "info", text: "loaded" },
        { at: "2026-10-09T09:00:01Z", level: "error", text: "boom" },
      ]);
      const wrapper = await openLog();
      expect(log.fetchModLog).toHaveBeenCalledTimes(1);
      expect(log.fetchModLog).toHaveBeenCalledWith("test-chips");
      expect(wrapper.get("[data-level=error]").text()).toContain("boom");
      expect(wrapper.get("[data-level=info]").text()).toContain("loaded");
    });

    it("says the runtime isn't running when the server has no log", async () => {
      log.fetchModLog.mockResolvedValue(null);
      const wrapper = await openLog();
      expect(wrapper.get("[data-testid=mod-log-test-chips]").text()).toContain("The mod's log shows here once the mod runtime is running.");
    });

    it("Refresh fetches again", async () => {
      log.fetchModLog.mockResolvedValue([]);
      const wrapper = await openLog();
      await wrapper.get("[data-testid=mod-log-refresh-test-chips]").trigger("click");
      await flushPromises();
      expect(log.fetchModLog).toHaveBeenCalledTimes(2);
    });
  });
});
