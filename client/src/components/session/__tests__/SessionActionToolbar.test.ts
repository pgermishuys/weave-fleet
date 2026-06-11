import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import SessionActionToolbar from "@/components/session/SessionActionToolbar.vue";

describe("SessionActionToolbar", () => {
  it("shows_actions_enabled_by_session_capabilities", () => {
    const wrapper = mount(SessionActionToolbar, {
      props: {
        canAbort: true,
        canResume: true,
        canStop: true,
        canArchive: true,
        canFork: true,
        canDelete: true,
      },
    });

    expect(wrapper.find("[data-testid='abort-button']").exists()).toBe(true);
    expect(wrapper.find("[data-testid='session-resume-button']").exists()).toBe(true);
    expect(wrapper.find("[data-testid='session-stop-button']").exists()).toBe(true);
    expect(wrapper.find("[data-testid='session-archived-fork-button']").exists()).toBe(true);
    expect(wrapper.find("[data-testid='session-archive-banner-button']").exists()).toBe(true);
    expect(wrapper.find("[data-testid='session-delete-button']").exists()).toBe(true);
  });

  it("hides_actions_disabled_by_session_capabilities", () => {
    const wrapper = mount(SessionActionToolbar, {
      props: {
        canAbort: false,
        canResume: false,
        canStop: false,
        canArchive: false,
        canFork: false,
        canDelete: false,
      },
    });

    expect(wrapper.find("[data-testid='abort-button']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='session-resume-button']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='session-stop-button']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='session-archived-fork-button']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='session-archive-banner-button']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='session-delete-button']").exists()).toBe(false);
  });
});
