import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
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

  it("shows_fork_off_with_its_reason_when_the_harness_cant_fork", () => {
    const reason = "Claude Code can't copy a conversation, so its sessions can't be forked.";
    const wrapper = mountToolbar({ canFork: false, forkDisabledReason: reason });

    const button = wrapper.get("[data-testid='session-archived-fork-button']");
    expect(button.attributes("disabled")).toBeDefined();
    expect(wrapper.get("[data-testid='session-fork-button-wrapper']").attributes("title")).toBe(reason);

    void button.trigger("click");
    expect(wrapper.emitted("fork")).toBeUndefined();
  });

  it("says_what_fork_does_when_it_is_on", () => {
    const wrapper = mountToolbar({ canFork: true });

    expect(wrapper.get("[data-testid='session-fork-button-wrapper']").attributes("title"))
      .toBe("Fork: a new session with a copy of this conversation");
    expect(wrapper.get("[data-testid='session-archived-fork-button']").attributes("disabled")).toBeUndefined();
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

  it("copies_the_folder_path_from_the_first_row_of_the_more_menu", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText } });
    try {
      const directory = "/home/sam/code/orbit-api-worktrees/fleet-session-7f3a9c21";
      const wrapper = mountToolbar({ canArchive: true, directory });
      const rows = wrapper.get("[data-testid='more-menu']").findAll("button");

      expect(rows[0].attributes("data-testid")).toBe("session-copy-directory-action");
      expect(rows[0].text()).toBe("Copy folder path");
      await rows[0].trigger("click");

      expect(writeText).toHaveBeenCalledWith(directory);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it("has_no_copy_row_without_a_folder", () => {
    const wrapper = mountToolbar({ directory: null });

    expect(wrapper.find("[data-testid='session-copy-directory-action']").exists()).toBe(false);
  });

  it("asks_to_export_from_the_more_menu", async () => {
    const wrapper = mountToolbar({});

    await wrapper.get("[data-testid='session-export-markdown-action']").trigger("click");

    expect(wrapper.text()).toContain("Export as Markdown…");
    expect(wrapper.emitted("export")).toHaveLength(1);
  });
});
