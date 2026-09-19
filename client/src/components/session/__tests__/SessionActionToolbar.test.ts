import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import SessionActionToolbar from "@/components/session/SessionActionToolbar.vue";

// The ⋯ menu's items render in a portal once it opens; the stubs render them inline.
const menuStubs = {
  DropdownMenu: { template: "<div><slot /></div>" },
  DropdownMenuTrigger: { template: "<div><slot /></div>" },
  DropdownMenuContent: { template: "<div data-testid=\"more-menu\"><slot /></div>" },
  DropdownMenuItem: {
    props: ["disabled", "variant"],
    emits: ["select"],
    template: "<button type=\"button\" :disabled=\"disabled\" @click=\"$emit('select', $event)\"><slot /></button>",
  },
  DropdownMenuSeparator: { template: "<hr>" },
  DropdownMenuShortcut: { template: "<span><slot /></span>" },
};

function mountToolbar(props: Record<string, unknown>) {
  return mount(SessionActionToolbar, {
    props: { hasSession: true, hasInstance: true, ...props },
    global: { stubs: menuStubs },
  });
}

describe("SessionActionToolbar", () => {
  it("shows_actions_enabled_by_session_capabilities", () => {
    const wrapper = mountToolbar({
      canAbort: true,
      canArchive: true,
      canFork: true,
      canDelete: true,
    });

    expect(wrapper.find("[data-testid='abort-button']").exists()).toBe(true);
    expect(wrapper.find("[data-testid='session-archived-fork-button']").exists()).toBe(true);
    expect(wrapper.find("[data-testid='session-archive-banner-button']").exists()).toBe(true);
    expect(wrapper.find("[data-testid='session-delete-button']").exists()).toBe(true);
    expect(wrapper.find("[data-testid='session-restore-action']").exists()).toBe(false);
  });

  it("hides_actions_disabled_by_session_capabilities", () => {
    const wrapper = mountToolbar({
      canAbort: false,
      canArchive: false,
      canFork: false,
      canDelete: false,
    });

    expect(wrapper.find("[data-testid='abort-button']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='session-archived-fork-button']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='session-archive-banner-button']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='session-delete-button']").exists()).toBe(false);
  });

  it("keeps_archive_and_delete_inside_the_more_menu", () => {
    const wrapper = mountToolbar({ canArchive: true, canDelete: true });
    const menu = wrapper.get("[data-testid='more-menu']");

    expect(menu.find("[data-testid='session-archive-banner-button']").exists()).toBe(true);
    expect(menu.find("[data-testid='session-delete-button']").exists()).toBe(true);
    expect(menu.find("[data-testid='session-rename-action']").exists()).toBe(true);
  });

  it("offers_restore_for_an_archived_session", async () => {
    const wrapper = mountToolbar({ canArchive: false, canRestore: true });

    await wrapper.get("[data-testid='session-restore-action']").trigger("click");

    expect(wrapper.emitted("restore")).toHaveLength(1);
  });
});
