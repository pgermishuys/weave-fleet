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

    await wrapper.find(".notice__btn").trigger("click");
    expect(later).toHaveBeenCalledOnce();

    await wrapper.find('[data-testid="notice-card"]').trigger("keydown", { key: "Escape" });
    expect(store.open).toBeNull();
  });

  it("settles into its chip when dismissed, and never opens as a card again", async () => {
    const { wrapper, store } = mountCard();
    store.post(ready);
    await advance(NOTICE_STARTUP_QUIET_MS + 500);

    const dismiss = wrapper.find('[data-testid="notice-dismiss"]');
    expect(dismiss.attributes("aria-label")).toBe("Dismiss");
    expect(dismiss.attributes("title")).toBe("Dismiss (Esc)");
    await dismiss.trigger("click");
    expect(store.open).toBeNull();
    expect(store.chips.map((notice) => notice.id)).toEqual([ready.id]);

    // A new page load: the dismissed notice comes back as its chip only.
    wrapper.unmount();
    setActivePinia(createPinia());
    const again = mountCard();
    again.store.post(ready);
    await advance(NOTICE_STARTUP_QUIET_MS + 2000);
    expect(again.wrapper.find('[data-testid="notice-card"]').exists()).toBe(false);
    expect(again.store.chips.map((notice) => notice.id)).toEqual([ready.id]);
  });

  it("goes away when dismissed if it has no chip, without running its Undo", async () => {
    const undo = vi.fn();
    const { wrapper, store } = mountCard();
    store.post({ id: "memory-saved-1", title: "Remembered for this repository", countdown: true, actions: [{ label: "Undo", run: undo }] });
    await advance(NOTICE_STARTUP_QUIET_MS + 500);

    await wrapper.find('[data-testid="notice-dismiss"]').trigger("click");
    expect(store.open).toBeNull();
    expect(store.has("memory-saved-1")).toBe(false);
    expect(undo).not.toHaveBeenCalled();
  });

  it("dismisses a card that asks something on Escape, keeping its chip", async () => {
    const { wrapper, store } = mountCard();
    store.post(ready);
    await advance(NOTICE_STARTUP_QUIET_MS + 500);
    store.update(ready.id, { title: "2 sessions are working" });
    await nextTick();

    await wrapper.find('[data-testid="notice-dismiss"]').trigger("keydown", { key: "Escape" });
    expect(store.open).toBeNull();
    expect(store.chips.map((notice) => notice.id)).toEqual([ready.id]);
  });

  it("sits in the bottom-right corner, lifted above a composer it would cover", async () => {
    vi.spyOn(window, "innerWidth", "get").mockReturnValue(1280);
    vi.spyOn(window, "innerHeight", "get").mockReturnValue(800);
    const composer = document.createElement("div");
    composer.setAttribute("data-notice-avoid", "");
    document.body.append(composer);
    // The right panel is open: the composer ends well left of the card.
    let composerRect = new DOMRect(400, 640, 500, 120);
    vi.spyOn(composer, "getBoundingClientRect").mockImplementation(() => composerRect);

    const { wrapper, store } = mountCard();
    store.post(ready);
    await advance(NOTICE_STARTUP_QUIET_MS + 500);
    const card = wrapper.find('[data-testid="notice-card"]').element as HTMLElement;
    vi.spyOn(card, "getBoundingClientRect").mockReturnValue(new DOMRect(1004, 660, 264, 100));
    window.dispatchEvent(new Event("resize"));
    await advance(50);
    expect(card.style.bottom).toBe("");

    // The right panel closes: the composer now reaches under the card, so the card lifts clear of its top.
    composerRect = new DOMRect(400, 640, 860, 120);
    window.dispatchEvent(new Event("resize"));
    await advance(50);
    expect(card.style.bottom).toBe(`${800 - 640 + 8}px`);
    composer.remove();
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
