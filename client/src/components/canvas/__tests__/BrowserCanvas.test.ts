import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import BrowserCanvas from "@/components/canvas/BrowserCanvas.vue";
import type { AppRunStatus } from "@/lib/domain-events";
import { RESTART_MARKER, useAppRunsStore } from "@/stores/app-runs";
import { serverCanvasTabId, useCanvasesStore } from "@/stores/canvases";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));
vi.mock("@/composables/use-weave-socket", () => ({ onReconnect: () => () => {} }));

const PREVIEW = "http://p1.localhost:41234";
const TAB = serverCanvasTabId("cv_1");

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

let app: { status: AppRunStatus; url: string | null; exitCode: number | null };
let output: string[];

function serve(): void {
  apiFetchMock.mockImplementation((path: string) => {
    if (path.endsWith("/browser/proxy")) return Promise.resolve(jsonResponse({ origin: PREVIEW }));
    if (path.includes("/output")) return Promise.resolve(jsonResponse({ lines: output, next: output.length }));
    if (path.endsWith("/restart")) {
      app = { status: "starting", url: null, exitCode: null };
      return Promise.resolve(jsonResponse({ id: "app_1", command: "npm run dev", ports: [], ...app }));
    }
    return Promise.resolve(jsonResponse({ id: "app_1", command: "npm run dev", ports: [5173], ...app }));
  });
}

function mountCanvas(url = "http://localhost:5173/"): VueWrapper {
  return mount(BrowserCanvas, { props: { sessionId: "s1", canvasId: "cv_1", url, appId: "app_1" }, attachTo: document.body });
}

function fromPage(wrapper: VueWrapper, data: unknown, origin = PREVIEW): void {
  const frame = wrapper.find("iframe").element as HTMLIFrameElement;
  window.dispatchEvent(new MessageEvent("message", { data, origin, source: frame.contentWindow }));
}

function setStatus(status: AppRunStatus, url: string | null = null, exitCode: number | null = null): void {
  useAppRunsStore().applyEvent({
    type: "app.updated",
    payload: { sessionId: "s1", appId: "app_1", command: "npm run dev", status, url, ports: [], exitCode, reason: "ready" },
  });
}

describe("BrowserCanvas", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    apiFetchMock.mockReset();
    app = { status: "running", url: "http://localhost:5173/", exitCode: null };
    output = [];
    serve();
  });

  afterEach(() => {
    document.body.innerHTML = "";
  });

  it("frames the page through its preview and shows where the page is", async () => {
    const wrapper = mountCanvas();
    await flushPromises();

    expect(wrapper.find("iframe").attributes("src")).toBe(`${PREVIEW}/`);
    fromPage(wrapper, { fleet: 1, type: "hello", hmr: "vite", href: `${PREVIEW}/`, title: "Shop" });
    fromPage(wrapper, { fleet: 1, type: "location", href: `${PREVIEW}/cart`, title: "Cart" });
    await flushPromises();

    expect((wrapper.find("input[aria-label='Address']").element as HTMLInputElement).value).toBe("http://localhost:5173/cart");
    expect(wrapper.find("[data-testid='browser-canvas-strip']").text()).toContain("running");
    wrapper.unmount();
  });

  it("ignores messages from other origins and old bridges", async () => {
    const wrapper = mountCanvas();
    await flushPromises();

    fromPage(wrapper, { fleet: 1, type: "location", href: "http://evil.test/" }, "http://evil.test");
    fromPage(wrapper, { type: "fleet-browser:location", href: `${PREVIEW}/old` });
    await flushPromises();

    expect((wrapper.find("input[aria-label='Address']").element as HTMLInputElement).value).toBe("http://localhost:5173/");
    wrapper.unmount();
  });

  it("pulses its tab when the page hot-updates or reloads itself, not when the canvas reloads it", async () => {
    const wrapper = mountCanvas();
    const canvases = useCanvasesStore();
    await flushPromises();

    fromPage(wrapper, { fleet: 1, type: "hello", hmr: "vite", href: `${PREVIEW}/` });
    expect(canvases.updatedAt[TAB]).toBeUndefined();

    fromPage(wrapper, { fleet: 1, type: "update" });
    const hotUpdate = canvases.updatedAt[TAB];
    expect(hotUpdate).toBeTypeOf("number");

    await wrapper.find("button[aria-label='Reload']").trigger("click");
    fromPage(wrapper, { fleet: 1, type: "hello", hmr: "vite", href: `${PREVIEW}/` });
    expect(canvases.updatedAt[TAB]).toBe(hotUpdate);

    vi.spyOn(Date, "now").mockReturnValue(hotUpdate + 5000);
    fromPage(wrapper, { fleet: 1, type: "hello", hmr: "vite", href: `${PREVIEW}/` });
    expect(canvases.updatedAt[TAB]).toBe(hotUpdate + 5000);
    vi.restoreAllMocks();
    wrapper.unmount();
  });

  it("offers Start for a stopped app, and shows the current run's output while it starts", async () => {
    app = { status: "stopped", url: null, exitCode: null };
    output = ["old run", "Error: crashed", RESTART_MARKER, "$ npm run dev", "> vite"];
    const wrapper = mountCanvas();
    await flushPromises();

    const panel = wrapper.find("[data-testid='browser-canvas-panel']");
    expect(panel.text()).toContain("The app is stopped");
    expect(wrapper.find("iframe").exists()).toBe(false);

    await panel.find("button").trigger("click");
    await flushPromises();

    expect(apiFetchMock).toHaveBeenCalledWith("/api/sessions/s1/apps/app_1/restart", { method: "POST" });
    const starting = wrapper.find("[data-testid='browser-canvas-panel']").text();
    expect(starting).toContain("Starting");
    expect(starting).toContain("> vite");
    expect(starting).not.toContain("crashed");
    wrapper.unmount();
  });

  it("shows why an app exited and its last output", async () => {
    app = { status: "exited", url: null, exitCode: 1 };
    output = ["Error: Cannot find module './routes'"];
    const wrapper = mountCanvas();
    await flushPromises();

    const panel = wrapper.find("[data-testid='browser-canvas-panel']").text();
    expect(panel).toContain("The app exited with code 1");
    expect(panel).toContain("Cannot find module");
    expect(wrapper.find("[data-testid='browser-canvas-strip']").text()).toContain("exited (1)");
    wrapper.unmount();
  });

  it("opens the output when a build fails", async () => {
    output = ["[vite] Internal server error: Unexpected token"];
    const wrapper = mountCanvas();
    await flushPromises();

    setStatus("build-failed", "http://localhost:5173/");
    await flushPromises();

    expect(wrapper.find("[data-testid='browser-canvas-strip']").text()).toContain("build failed");
    expect(wrapper.find("pre[aria-label='Output']").text()).toContain("Internal server error");
    wrapper.unmount();
  });

  it("follows its app's page when the tab has none of its own (started from the + menu)", async () => {
    app = { status: "starting", url: null, exitCode: null };
    const wrapper = mountCanvas("");
    await flushPromises();
    expect(wrapper.find("[data-testid='browser-canvas-panel']").text()).toContain("Starting");

    setStatus("running", "http://localhost:5173/");
    await flushPromises();

    expect(apiFetchMock).toHaveBeenCalledWith("/api/sessions/s1/browser/proxy", expect.objectContaining({ body: JSON.stringify({ url: "http://localhost:5173/" }) }));
    expect(wrapper.find("iframe").attributes("src")).toBe(`${PREVIEW}/`);
    wrapper.unmount();
  });
});
