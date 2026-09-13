import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import { ref, type DefineComponent } from "vue";
import SessionDetailHeaderComponent from "@/components/session/SessionDetailHeader.vue";
import { useSidebarStore } from "@/stores/sidebar";

interface HeaderProps {
  id: string;
  title?: string;
  activityStatus?: string | null;
  lifecycleStatus?: string | null;
  retryAttempt?: number | null;
}

// The named "actions" slot trips the mount() typings in @vue/test-utils 2.2.7.
const SessionDetailHeader = SessionDetailHeaderComponent as unknown as DefineComponent<HeaderProps>;

vi.mock("@/composables/use-harnesses", () => ({
  useHarnesses: () => ({ harnesses: ref([]) }),
}));

function mountHeader(props: Partial<HeaderProps> = {}) {
  return mount(SessionDetailHeader, {
    props: {
      id: "session-1",
      title: "Consolidate status indicators",
      activityStatus: "busy",
      lifecycleStatus: "running",
      ...props,
    },
    global: {
      stubs: {
        SessionContextChips: true,
        SessionAnalyticsPopover: true,
      },
    },
  });
}

describe("SessionDetailHeader status", () => {
  it("shows no visible status while a sessions list is on screen", () => {
    useSidebarStore().registerSessionList();
    const wrapper = mountHeader();

    expect(wrapper.find("[data-testid='session-header-glyph']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='session-retry-note']").exists()).toBe(false);
  });

  it("keeps a screen-reader status with the activity for tests and assistive tech", () => {
    const wrapper = mountHeader({ activityStatus: "idle" });
    const status = wrapper.get("[data-testid='session-status-indicator']");

    expect(status.attributes("role")).toBe("status");
    expect(status.classes()).toContain("sr-only");
    expect(status.attributes("data-status")).toBe("idle");
    expect(status.text()).toBe("Idle");
  });

  it("shows the row's glyph before the title when no sessions list is on screen", async () => {
    const release = useSidebarStore().registerSessionList();
    const wrapper = mountHeader();
    expect(wrapper.find("[data-testid='session-header-glyph']").exists()).toBe(false);

    release();
    await wrapper.vm.$nextTick();

    const glyph = wrapper.get("[data-testid='session-header-glyph']");
    expect(glyph.classes()).toContain("status-glyph--working");
    expect(glyph.attributes("aria-label")).toBe("Working");
  });

  it("names a retry and its attempt next to the title", () => {
    useSidebarStore().registerSessionList();
    const wrapper = mountHeader({ activityStatus: "retry", retryAttempt: 3 });

    expect(wrapper.get("[data-testid='session-retry-note']").text()).toBe("Retrying · attempt 3");
    expect(wrapper.get("[data-testid='session-status-indicator']").attributes("data-status")).toBe("retry");
  });
});
