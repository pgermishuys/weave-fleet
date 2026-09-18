import { DOMWrapper, flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { computed, defineComponent, h, ref, shallowRef } from "vue";
import HarnessSetupWizard from "@/components/harness-setup/HarnessSetupWizard.vue";
import type { HarnessInfo } from "@/api/client";
import { useAppShellStore } from "@/stores/app-shell";
import { HARNESS_SETUP_DONE_PREFERENCE, useHarnessSetupStore } from "@/stores/harness-setup";
import { usePreferencesStore } from "@/stores/preferences";

const mocks = vi.hoisted(() => ({
  navigate: vi.fn(),
  put: vi.fn(),
}));

const harnesses = ref<HarnessInfo[]>([]);
const noHarnessReason = shallowRef<string | null>(null);

vi.mock("@tanstack/vue-router", () => ({
  useRouter: () => ({ navigate: mocks.navigate }),
}));

vi.mock("@/composables/use-enabled-harnesses", () => ({
  useEnabledHarnesses: () => ({
    harnesses: computed(() => harnesses.value),
    enabledHarnesses: computed(() => harnesses.value.filter((harness) => harness.available && harness.userEnabled)),
    defaultHarnessType: computed(() => "opencode"),
    noHarnessReason: computed(() => noHarnessReason.value),
  }),
}));

vi.mock("@/composables/use-harnesses", () => ({
  refreshAllHarnesses: vi.fn(),
}));

vi.mock("@/api/client", () => ({
  api: { GET: vi.fn(async () => ({ data: {}, error: undefined })), PUT: mocks.put },
}));

// The rows have their own tests; here they only need to be there.
vi.mock("@/components/harness-setup/HarnessSetupRows.vue", () => ({
  default: defineComponent({
    name: "HarnessSetupRows",
    setup: (_props, { expose }) => {
      expose({ closeTerminal: async () => {} });
      return () => h("div", { "data-testid": "harness-setup-rows" });
    },
  }),
}));

const capabilities = {
  requiresInitialPrompt: false,
  supportsAgents: true,
  supportsModelSelection: true,
  supportsCommands: true,
  supportsForking: true,
  supportsResume: true,
  supportsImageAttachments: true,
  supportsStreaming: true,
  supportsDelegation: true,
};

const missingOpenCode: HarnessInfo = {
  type: "opencode",
  displayName: "OpenCode",
  available: false,
  userEnabled: true,
  state: "not-installed",
  reason: "OpenCode isn't installed.",
  capabilities,
  setup: { installCommand: "curl -fsSL https://opencode.ai/install | bash" },
};

const readyOpenCode: HarnessInfo = { ...missingOpenCode, available: true, state: "ready", reason: null, version: "1.18.30" };

let wrapper: VueWrapper | null = null;

/**
 * The wizard as it mounts at startup, once preferences and harnesses have loaded. The dialog is portaled to
 * <body>, so this returns the page to look in.
 */
async function launch(options: { done?: boolean; cloud?: boolean } = {}): Promise<DOMWrapper<Element>> {
  const preferences = usePreferencesStore();
  preferences.preferences = options.done ? { [HARNESS_SETUP_DONE_PREFERENCE]: "true" } : {};
  preferences.hasFetched = true;
  useAppShellStore().config = { ...useAppShellStore().config, cloudMode: options.cloud ?? false };
  wrapper = mount(HarnessSetupWizard, { attachTo: document.body, global: { stubs: { teleport: false } } });
  await flushPromises();
  return new DOMWrapper(document.body);
}

beforeEach(() => {
  harnesses.value = [missingOpenCode];
  noHarnessReason.value = "OpenCode isn't installed.";
  mocks.navigate.mockReset().mockResolvedValue(undefined);
  mocks.put.mockReset().mockResolvedValue({ data: undefined, error: undefined });
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = null;
});

describe("HarnessSetupWizard", () => {
  it("opens on the first launch when no harness is ready", async () => {
    const view = await launch();

    expect(useHarnessSetupStore().isOpen).toBe(true);
    expect(view.text()).toContain("Welcome to Weave Fleet");
    expect(view.text()).toContain("Fleet runs your sessions through a harness");
  });

  it("stays closed once setup was finished or skipped, in cloud mode, and when a harness is ready", async () => {
    await launch({ done: true });
    expect(useHarnessSetupStore().isOpen).toBe(false);
    wrapper?.unmount();

    await launch({ cloud: true });
    expect(useHarnessSetupStore().isOpen).toBe(false);
    wrapper?.unmount();

    noHarnessReason.value = null;
    harnesses.value = [readyOpenCode];
    await launch();
    expect(useHarnessSetupStore().isOpen).toBe(false);
  });

  it("Skip setup closes it and remembers, so it doesn't open again", async () => {
    const view = await launch();

    await view.get("[data-testid='harness-setup-skip']").trigger("click");
    await flushPromises();

    expect(useHarnessSetupStore().isOpen).toBe(false);
    expect(mocks.put).toHaveBeenCalledWith("/api/preferences/{key}", {
      params: { path: { key: HARNESS_SETUP_DONE_PREFERENCE } },
      body: { value: "true" },
    });
  });

  it("goes on only once a harness is ready, then starts a session", async () => {
    const view = await launch();
    await view.get("[data-testid='harness-setup-get-started']").trigger("click");
    await flushPromises();

    expect(view.find("[data-testid='harness-setup-rows']").exists()).toBe(true);
    expect(view.get("[data-testid='harness-setup-continue']").attributes("disabled")).toBeDefined();

    harnesses.value = [readyOpenCode];
    await flushPromises();
    await view.get("[data-testid='harness-setup-continue']").trigger("click");
    await flushPromises();

    expect(view.text()).toContain("You're all set");
    expect(view.text()).toContain("OpenCode 1.18.30 is ready.");

    await view.get("[data-testid='harness-setup-start-session']").trigger("click");
    await flushPromises();

    expect(useHarnessSetupStore().isOpen).toBe(false);
    expect(mocks.navigate).toHaveBeenCalledWith({ to: "/sessions/new", search: { projectId: undefined, source: undefined } });
  });

  it("opens at the harness step from the banner, the new-session box or Settings", async () => {
    noHarnessReason.value = null;
    const view = await launch({ done: true });

    useHarnessSetupStore().open("harnesses");
    await flushPromises();

    expect(view.text()).toContain("Install a harness");
    expect(view.text()).toContain("You need at least one.");
  });
});
