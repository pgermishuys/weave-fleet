import { mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { ref } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("@tanstack/vue-router", () => ({
  useRouter: () => ({ navigate: vi.fn() }),
  useLocation: () => ref("/"),
}));
vi.mock("@/api/client", () => ({
  api: { GET: vi.fn().mockResolvedValue({ data: { statuses: [] }, error: undefined, response: { status: 200 } }) },
}));
vi.mock("@/lib/api-client", () => ({ apiFetchOn: vi.fn(), apiFetch: vi.fn() }));
vi.mock("@/composables/use-board-feature", () => ({ useBoardFeature: () => ({ isBoardFeatureEnabled: ref(false) }) }));
vi.mock("@/composables/use-workflows-feature", () => ({ useWorkflowsFeature: () => ({ isWorkflowsEnabled: ref(false) }) }));
vi.mock("@/stores/workflows", () => ({ useWorkflowsStore: () => ({ ensureLoaded: vi.fn() }) }));
vi.mock("@/composables/use-whats-new", () => ({ useWhatsNew: () => ({ openWhatsNew: vi.fn() }) }));
vi.mock("@/composables/use-media-query", () => ({ useIsMobileNav: () => ref(false) }));

import IconRail from "@/components/layout/IconRail.vue";
import { useModsStore } from "@/stores/mods";

// The Help menu's items render in a portal once it opens; the stubs render them inline.
const menuStubs = {
  DropdownMenu: { template: "<div><slot /></div>" },
  DropdownMenuTrigger: { template: "<div><slot /></div>" },
  DropdownMenuContent: { template: "<div><slot /></div>" },
  DropdownMenuItem: { emits: ["select"], template: "<button type=\"button\" @click=\"$emit('select', $event)\"><slot /></button>" },
  DropdownMenuSeparator: { template: "<hr>" },
};

function mountRail(modsSwitch: { on: boolean; safeMode: boolean } | null) {
  const pinia = createPinia();
  setActivePinia(pinia);
  const store = useModsStore();
  store.modsSwitch = modsSwitch;
  const setSafeMode = vi.spyOn(store, "setSafeMode").mockResolvedValue({} as never);
  const wrapper = mount(IconRail, { global: { plugins: [pinia], stubs: menuStubs } });
  return { wrapper, setSafeMode };
}

describe("the Help menu's mods item", () => {
  beforeEach(() => vi.clearAllMocks());

  it("offers Start without mods while the Mods switch is on, and starts safe mode", async () => {
    const { wrapper, setSafeMode } = mountRail({ on: true, safeMode: false });

    const item = wrapper.get("[data-testid=rail-start-without-mods]");
    expect(item.text()).toBe("Start without mods");
    await item.trigger("click");

    expect(setSafeMode).toHaveBeenCalledWith(true);
  });

  it("offers Turn mods back on in safe mode, and turns them on", async () => {
    const { wrapper, setSafeMode } = mountRail({ on: true, safeMode: true });

    const item = wrapper.get("[data-testid=rail-start-without-mods]");
    expect(item.text()).toBe("Turn mods back on");
    await item.trigger("click");

    expect(setSafeMode).toHaveBeenCalledWith(false);
  });

  it.each([[{ on: false, safeMode: false }], [null]])("is not there while the Mods switch is off or unread (%j)", (state) => {
    const { wrapper } = mountRail(state);

    expect(wrapper.find("[data-testid=rail-start-without-mods]").exists()).toBe(false);
  });
});
