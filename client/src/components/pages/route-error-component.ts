import { h } from "vue";
import type { ErrorRouteComponent } from "@tanstack/vue-router";
import RouteErrorPage from "./RouteErrorPage.vue";

/** The router's defaultErrorComponent, which it types as a render function: draws RouteErrorPage. */
export const routeErrorComponent: ErrorRouteComponent = (props) => h(RouteErrorPage, props);
