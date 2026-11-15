import { createRoute, type AnyRootRoute, type AnyRoute } from "@tanstack/vue-router";
import { defineComponent } from "vue";

/**
 * Mock mode only (router.ts): a route that throws while it renders, so the UI crawl can prove it fails a screen that
 * lands on the error page (scripts/ui-crawl/run.sh --self-test). Never in a real build.
 */
export const CRAWL_SELF_TEST_PATH = "/crawl-self-test/throws";
export const CRAWL_SELF_TEST_ERROR = "Crawl self-test: this screen throws while it renders";

/** The same tree with the throwing route added. Its type stays the tree's own, so links can't name the route. */
export function withCrawlSelfTest<T>(tree: T): T {
  // Made in here, not at the top of the module, so a real build (which never calls this) drops all of it.
  const Throws = defineComponent({
    name: "CrawlSelfTestThrows",
    setup() {
      return () => {
        throw new Error(CRAWL_SELF_TEST_ERROR);
      };
    },
  });
  const root = tree as unknown as AnyRootRoute;
  const route = createRoute({ getParentRoute: () => root, path: CRAWL_SELF_TEST_PATH, component: Throws });
  const children = (root.children ?? {}) as Record<string, AnyRoute>;
  return root._addFileChildren({ ...children, CrawlSelfTestRoute: route }) as unknown as T;
}
