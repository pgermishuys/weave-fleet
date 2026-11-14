// The phone's Needs you says nothing needs you only once a machine has answered. Its feed can be live (the hub
// connected) or polling (the hub failed) before the first read is back; it used to read that as "answered".
import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import { shallowRef } from "vue";
import InboxPage from "@/components/phone/InboxPage.vue";
import { INBOX } from "@/composables/phone/use-inbox";
import type { InboxMachineState } from "@/lib/phone/inbox";

vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn(), history: { back: vi.fn() } }) }));
vi.mock("@/composables/phone/use-push-subscription", () => ({
  usePushSubscription: () => ({ refresh: vi.fn(async () => undefined), subscribed: shallowRef(false), permissionRevoked: shallowRef(false) }),
}));
vi.mock("@/composables/use-enabled-harnesses", () => ({ useEnabledHarnesses: () => ({ noHarnessReason: shallowRef(null) }) }));

function machine(extra: Partial<InboxMachineState>): InboxMachineState {
  return { id: "home", name: "Lighthouse", isHome: true, status: "live", lastHeardAt: null, sessions: [], asks: {}, ...extra };
}

function mountWith(machines: InboxMachineState[]) {
  const inbox = {
    inbox: shallowRef({ needsYou: [], working: [], finished: [] }),
    machines: shallowRef(machines),
    loading: shallowRef(false),
    now: shallowRef(Date.parse("2026-11-14T10:00:00Z")),
    answerPermission: vi.fn(),
    answerQuestion: vi.fn(),
    setArchived: vi.fn(),
    refreshAll: vi.fn(async () => undefined),
    targetFor: vi.fn(() => null),
  };
  return mount(InboxPage, { props: { tab: "needs-you" }, global: { provide: { [INBOX as symbol]: inbox } } });
}

describe("the phone's Needs you on its first load", () => {
  it.each(["live", "polling"] as const)("waits for a %s machine's first answer before saying nothing needs you", (status) => {
    expect(mountWith([machine({ status })]).text()).not.toContain("Nothing needs you right now");
  });

  it("says nothing needs you once the machine has answered", () => {
    expect(mountWith([machine({ lastHeardAt: Date.parse("2026-11-14T09:59:58Z") })]).text()).toContain("Nothing needs you right now");
  });

  it("doesn't wait on a machine it can't reach", () => {
    const wrapper = mountWith([machine({ status: "unreachable" })]);
    expect(wrapper.text()).toContain("Nothing needs you right now");
  });
});
