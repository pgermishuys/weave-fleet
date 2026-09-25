import { mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import MessageBubble from "@/components/session/MessageBubble.vue";
import type { ToolCardItem } from "@/components/session/activity-stream-tool-card";

const shotTool: ToolCardItem = {
  id: "part-shot",
  title: "Shop · 1280×800",
  kind: "fleet_browser_screenshot",
  status: "Completed",
  output: "Screenshot of http://localhost:5173/ at 1280×800.",
  initiallyCollapsed: true,
  canvasId: "cv_1",
  screenshot: { url: "/api/sessions/ses-1/screenshots/shot_1", width: 1280, height: 800 },
};

let wrapper: { unmount(): void } | undefined;

function bubble(tools: ToolCardItem[]) {
  const mounted = mount(MessageBubble, {
    attachTo: document.body,
    // The real Teleport, so the lightbox lands on the page the way it does in the app.
    global: { plugins: [createPinia()], stubs: { teleport: false } },
    props: {
      author: "Agent",
      role: "assistant",
      body: "Here's the page.",
      tools,
      showIdentity: true,
      clusterPosition: "single",
    },
  });
  wrapper = mounted;
  return mounted;
}

function lightbox(): HTMLElement | null {
  return document.body.querySelector("[data-testid='image-lightbox']");
}

describe("a screenshot in the conversation", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  afterEach(() => {
    wrapper?.unmount();
    wrapper = undefined;
  });

  it("shows a thumbnail under the call, even with the call's row folded", () => {
    const view = bubble([shotTool]);

    const thumb = view.get("[data-testid='tool-screenshot']");
    const img = thumb.get("img");
    expect(img.attributes("src")).toBe("/api/sessions/ses-1/screenshots/shot_1");
    // The size it was taken at, so the stream keeps its place while the image loads.
    expect(img.attributes("width")).toBe("1280");
    expect(img.attributes("height")).toBe("800");
    expect(thumb.attributes("aria-label")).toBe("Open screenshot: Shop · 1280×800");
    expect(view.get("[data-testid='tool-card']").attributes("open")).toBeUndefined();
    expect(lightbox()).toBeNull();
  });

  it("shows nothing extra for a call without one", () => {
    const view = bubble([{ ...shotTool, screenshot: undefined }]);

    expect(view.find("[data-testid='tool-screenshot']").exists()).toBe(false);
  });

  it("opens full size on click and closes on Esc, handing focus back to the thumbnail", async () => {
    const view = bubble([shotTool]);
    const thumb = view.get("[data-testid='tool-screenshot']");
    (thumb.element as HTMLButtonElement).focus();

    await thumb.trigger("click");
    await view.vm.$nextTick();

    const opened = lightbox();
    expect(opened).not.toBeNull();
    expect(opened!.getAttribute("role")).toBe("dialog");
    expect(opened!.querySelector("img")!.getAttribute("src")).toBe("/api/sessions/ses-1/screenshots/shot_1");
    expect(document.activeElement).toBe(opened!.querySelector("[data-testid='image-lightbox-close']"));

    document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    await view.vm.$nextTick();
    await view.vm.$nextTick();

    expect(lightbox()).toBeNull();
    expect(document.activeElement).toBe(thumb.element);
  });

  it("closes from the backdrop and the close button, but not from the picture", async () => {
    const view = bubble([shotTool]);
    const thumb = view.get("[data-testid='tool-screenshot']");

    await thumb.trigger("click");
    lightbox()!.querySelector<HTMLElement>("[data-testid='image-lightbox-image']")!.click();
    await view.vm.$nextTick();
    expect(lightbox()).not.toBeNull();

    lightbox()!.querySelector<HTMLElement>("[data-testid='image-lightbox-close']")!.click();
    await view.vm.$nextTick();
    expect(lightbox()).toBeNull();

    await thumb.trigger("click");
    lightbox()!.click();
    await view.vm.$nextTick();
    expect(lightbox()).toBeNull();
  });

  it("disappears rather than showing a broken image when the shot is gone", async () => {
    const view = bubble([shotTool]);

    await view.get("[data-testid='tool-screenshot'] img").trigger("error");

    expect(view.find("[data-testid='tool-screenshot']").exists()).toBe(false);
  });
});
