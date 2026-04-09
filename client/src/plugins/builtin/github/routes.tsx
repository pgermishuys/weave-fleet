import { lazy } from "react";
import type { FleetPluginRoute } from "@/plugins/types";

const GitHubPage = lazy(() => import("@/app/github/page"));
const GitHubRepoPage = lazy(() => import("@/app/github/[owner]/[repo]/_page-client"));

export const githubRoutes: readonly FleetPluginRoute[] = [
  {
    pluginId: "github",
    path: "/github",
    viewId: "github",
    element: <GitHubPage />,
  },
  {
    pluginId: "github",
    path: "/github/:owner/:repo",
    viewId: "github",
    element: <GitHubRepoPage />,
  },
];
