import { mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ref } from "vue";
import type { SessionListItem } from "@/api/client";
import StatusBar from "@/components/layout/StatusBar.vue";
import { useSessionsStore } from "@/stores/sessions";

/**
 * The status bar as it was before mods, with and without a session: today's DOM. Imports only what existed before
 * mods, so it passes before them and after them alike. (The router is stood in for: the bar reads the route now, and
 * a session view is the route a session is on screen in.)
 */
vi.mock("@tanstack/vue-router", () => ({
  useRouter: () => ({}),
  useLocation: () => ref("/sessions/s1"),
}));
vi.mock("@/api/client", () => ({ api: { GET: vi.fn(async () => ({ data: [], response: { ok: true, status: 200 } })) } }));

const mounted: Array<{ unmount: () => void }> = [];
function mountBar() {
  const wrapper = mount(StatusBar);
  mounted.push(wrapper);
  return wrapper;
}

/** The DOM without Vue's v-if placeholders and comments. */
const html = (w: { element: Element }) => w.element.outerHTML.replace(/<!--[\s\S]*?-->/g, "");

function openSession(id = "s1"): void {
  const sessions = useSessionsStore();
  sessions.setSessions([
    { session: { id, title: "Session" }, instanceId: "i1", activityStatus: "idle", sessionStatus: "idle", totalTokens: 1200 } as unknown as SessionListItem,
  ]);
  sessions.setActiveSessionId(id);
}

describe("StatusBar without a mod chip", () => {
  beforeEach(() => globalThis.localStorage?.clear());
  afterEach(() => {
    for (const wrapper of mounted.splice(0)) wrapper.unmount();
  });

  it("draws the same with no session", () => {
    expect(html(mountBar())).toMatchSnapshot();
  });

  it("draws the same with a session", () => {
    openSession();
    expect(html(mountBar())).toMatchSnapshot();
  });
});
