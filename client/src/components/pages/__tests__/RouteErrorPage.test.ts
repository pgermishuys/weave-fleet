import { flushPromises, mount } from "@vue/test-utils";
import { h } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  RouterProvider,
  createMemoryHistory,
  createRootRoute,
  createRoute,
  createRouter,
  Outlet,
} from "@tanstack/vue-router";
import RouteErrorPage from "@/components/pages/RouteErrorPage.vue";
import { routeErrorComponent } from "@/components/pages/route-error-component";
import { CRAWL_SELF_TEST_ERROR, CRAWL_SELF_TEST_PATH, withCrawlSelfTest } from "@/lib/crawl-self-test";

describe("the route error page", () => {
  let consoleError: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    consoleError = vi.spyOn(console, "error").mockImplementation(() => undefined);
    localStorage.clear();
  });
  afterEach(() => {
    consoleError.mockRestore();
    localStorage.clear();
  });

  it("logs the error it shows, with the address, and marks itself for the crawl", () => {
    window.history.replaceState(null, "", "/analytics?from=2026-11-01");
    const error = new ReferenceError("formatShortDate is not defined");

    const wrapper = mount(RouteErrorPage, { props: { error, reset: vi.fn() } });

    expect(consoleError).toHaveBeenCalledWith(
      "[fleet] /analytics?from=2026-11-01 stopped rendering: formatShortDate is not defined",
      error,
    );
    expect(wrapper.find("[data-fleet-error-fallback]").exists()).toBe(true);
    expect(wrapper.text()).toContain("This page stopped with an error.");
    expect(wrapper.text()).toContain("formatShortDate is not defined");
  });

  it("writes it to the diagnostics log when diagnostics are on", () => {
    localStorage.setItem("fleet:diag-enabled", "1");
    vi.spyOn(console, "debug").mockImplementation(() => undefined);

    mount(RouteErrorPage, { props: { error: new Error("Boom while drawing") } });

    const log = JSON.parse(localStorage.getItem("fleet:message-diagnostics") ?? "[]");
    expect(log).toEqual([
      expect.objectContaining({ cat: "render-error", msg: "Boom while drawing", data: expect.objectContaining({ where: expect.any(String) }) }),
    ]);
  });

  it("tries again with the router's reset", async () => {
    const reset = vi.fn();
    const wrapper = mount(RouteErrorPage, { props: { error: new Error("Boom"), reset } });

    const button = wrapper.findAll("button").find((b) => b.text() === "Try again");
    expect(button).toBeDefined();
    await button!.trigger("click");

    expect(reset).toHaveBeenCalledOnce();
  });

  it("is what the router draws, and logs, when a route throws while it renders", async () => {
    const rootRoute = createRootRoute({ component: () => h("main", [h(Outlet)]) });
    const routeTree = withCrawlSelfTest(rootRoute.addChildren([
      createRoute({ getParentRoute: () => rootRoute, path: "/", component: () => h("p", "Home") }),
    ]));
    const router = createRouter({
      routeTree,
      history: createMemoryHistory({ initialEntries: [CRAWL_SELF_TEST_PATH] }),
      defaultErrorComponent: routeErrorComponent,
    });
    await router.load();

    const wrapper = mount(RouterProvider, { props: { router } });
    await flushPromises();

    expect(wrapper.find("main [data-fleet-error-fallback]").exists()).toBe(true);
    expect(wrapper.text()).toContain(CRAWL_SELF_TEST_ERROR);
    expect(consoleError).toHaveBeenCalledWith(expect.stringContaining(`stopped rendering: ${CRAWL_SELF_TEST_ERROR}`), expect.any(Error));
  });
});
