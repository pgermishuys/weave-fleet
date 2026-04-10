import { githubManifest } from "@/integrations/github/manifest";
import { GitHubPanel } from "@/components/layout/github-panel";
import type { FleetPluginManifest } from "@/plugins/types";
import { githubRoutes } from "./routes";
import { GitHubPluginStartupHook } from "./runtime";

export const githubPluginManifest: FleetPluginManifest = {
  descriptor: {
    id: "github",
    displayName: "GitHub",
    trustLevel: "built-in",
    hasFrontend: true,
    hasBackend: true,
  },
  contributions: {
    sidebarItems: [
      {
        viewId: "github",
        label: "GitHub",
        icon: githubManifest.icon,
        defaultPath: "/github",
        order: 100,
      },
    ],
    sidebarPanels: [
      {
        viewId: "github",
        component: GitHubPanel,
        order: 100,
      },
    ],
    routes: githubRoutes,
    settingsSections: githubManifest.settingsComponent
      ? [
          {
            id: "github-settings",
            title: "GitHub",
            component: githubManifest.settingsComponent,
            icon: githubManifest.icon,
            order: 100,
          },
        ]
      : undefined,
    startupHooks: [
      {
        id: "github-cache-warmer",
        component: GitHubPluginStartupHook,
        order: 100,
      },
    ],
    contextResolvers: [
      {
        id: "github-context-resolver",
        resolveContext: githubManifest.resolveContext,
      },
    ],
    sessionSources: [
      {
        id: "github-issue-source",
        sourceKey: {
          providerId: "builtin.github",
          sourceType: "github-issue",
        },
        label: "GitHub Issue",
        description: "Attach issue context to a session using backend-resolved GitHub data.",
        order: 100,
      },
      {
        id: "github-pr-source",
        sourceKey: {
          providerId: "builtin.github",
          sourceType: "github-pull-request",
        },
        label: "GitHub Pull Request",
        description: "Attach pull request context to a session using backend-resolved GitHub data.",
        order: 110,
      },
    ],
  },
};
