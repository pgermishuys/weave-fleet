import { createRouter } from "@tanstack/vue-router";
import { routeErrorComponent } from "@/components/pages/route-error-component";
import { withCrawlSelfTest } from "@/lib/crawl-self-test";
import { routeTree } from "./routeTree.gen";

export const router = createRouter({
  // Mock mode adds a route that throws, for the UI crawl's self-test.
  routeTree: import.meta.env.MODE === "mock" ? withCrawlSelfTest(routeTree) : routeTree,
  // Every route gets Fleet's error page, which logs what it caught (the router's own logs nothing in a build).
  defaultErrorComponent: routeErrorComponent,
});

declare module "@tanstack/vue-router" {
  interface Register {
    router: typeof router;
  }
}
