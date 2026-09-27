import { enableAutoUnmount, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { nextTick } from "vue";
import NoticeCard from "@/components/notices/NoticeCard.vue";
import { NOTICE_HOLD_MS, NOTICE_STARTUP_QUIET_MS, NOTICE_TYPING_QUIET_MS, useNoticesStore } from "@/stores/notices";

enableAutoUnmount(afterEach);

let focused = true;
let visibility: DocumentVisibilityState = "visible";

function mountCard() {
  const wrapper = mount(NoticeCard, { attachTo: document.body });
  return { wrapper, store: useNoticesStore() };
}

async function advance(ms: number): Promise<void> {
  vi.advanceTimersByTime(ms);
  await nextTick();
}

const ready = { id: "update:app:0.37.0", title: "Fleet 0.37.0 is ready", body: "Installs when you restart.", chip: "Fleet 0.37.0 ready" };

describe("NoticeCard", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    localStorage.clear();
    setActivePinia(createPinia());
    focused = true;
    visibility = "visible";
    vi.spyOn(document, "hasFocus").mockImplementation(() => focused);
    vi.spyOn(document, "visibilityState", "get").mockImplementation(() => visibility);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it("opens a few seconds after Fleet opens, not on the first paint", async () => {
    const { wrapper, store } = mountCard();
    store.post(ready);
    await nextTick();

    await advance(NOTICE_STARTUP_QUIET_MS - 600);
    expect(wrapper.find('[data-testid="notice-card"]').exists()).toBe(false);

    await advance(1000);
    expect(wrapper.find('[data-testid="notice-card"]').text()).toContain("Fleet 0.37.0 is ready");
  });

  it("waits while you type and opens once you pause", async () => {
    const input = document.createElement("textarea");
    document.body.append(input);
    const { wrapper, store } = mountCard();
    await advance(NOTICE_STARTUP_QUIET_MS);

    input.dispatchEvent(new KeyboardEvent("keydown", { key: "a", bubbles: true }));
    store.post(ready);
    await nextTick();

    await advance(NOTICE_TYPING_QUIET_MS - 600);
    input.dispatchEvent(new KeyboardEvent("keydown", { key: "b", bubbles: true }));
    await advance(NOTICE_TYPING_QUIET_MS - 600);
    expect(wrapper.find('[data-testid="notice-card"]').exists()).toBe(false);

    await advance(1200);
    expect(wrapper.find('[data-testid="notice-card"]').exists()).toBe(true);
    input.remove();
  });

  it("waits until you're back in Fleet", async () => {
    focused = false;
    const { wrapper, store } = mountCard();
    store.post(ready);
    await advance(NOTICE_STARTUP_QUIET_MS + 2000);
    expect(wrapper.find('[data-testid="notice-card"]').exists()).toBe(false);

    focused = true;
    window.dispatchEvent(new Event("focus"));
    await nextTick();
    expect(wrapper.find('[data-testid="notice-card"]').exists()).toBe(true);
  });

  it("settles into its chip after a while, and holds while hovered", async () => {
    const { wrapper, store } = mountCard();
    store.post(ready);
    await advance(NOTICE_STARTUP_QUIET_MS + 500);
    const card = wrapper.find('[data-testid="notice-card"]');
    expect(card.exists()).toBe(true);

    await card.trigger("mouseenter");
    await advance(NOTICE_HOLD_MS * 2);
    expect(store.open?.id).toBe(ready.id);

    await card.trigger("mouseleave");
    await advance(NOTICE_HOLD_MS);
    expect(store.open).toBeNull();
    expect(store.chips.map((notice) => notice.id)).toEqual([ready.id]);
  });

  it("stays open when you opened it yourself", async () => {
    const { store } = mountCard();
    store.post(ready);
    await advance(NOTICE_STARTUP_QUIET_MS + 500);
    store.settle();

    store.reopen(ready.id);
    await advance(NOTICE_HOLD_MS * 3);
    expect(store.open?.id).toBe(ready.id);
  });

  it("stays open once it asks something", async () => {
    const { store } = mountCard();
    store.post(ready);
    await advance(NOTICE_STARTUP_QUIET_MS + 500);

    store.update(ready.id, { title: "2 sessions are working" });
    await advance(NOTICE_HOLD_MS * 3);
    expect(store.open?.title).toBe("2 sessions are working");
  });

  it("runs an action and settles on Escape", async () => {
    const later = vi.fn();
    const { wrapper, store } = mountCard();
    store.post({ ...ready, actions: [{ label: "Later", run: later }] });
    await advance(NOTICE_STARTUP_QUIET_MS + 500);

    await wrapper.find("button").trigger("click");
    expect(later).toHaveBeenCalledOnce();

    await wrapper.find('[data-testid="notice-card"]').trigger("keydown", { key: "Escape" });
    expect(store.open).toBeNull();
  });

  it("links to the release notes", async () => {
    const { wrapper, store } = mountCard();
    store.post({ ...ready, link: { label: "What's new", href: "https://example.test/notes" } });
    await advance(NOTICE_STARTUP_QUIET_MS + 500);

    const link = wrapper.find("a");
    expect(link.text()).toBe("What's new");
    expect(link.attributes("href")).toBe("https://example.test/notes");
    expect(link.attributes("target")).toBe("_blank");
  });
});
