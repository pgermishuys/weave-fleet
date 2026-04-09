import { GitHubRepoCacheWarmer } from "@/integrations/github/components/repo-cache-warmer";

export function GitHubPluginStartupHook() {
  return <GitHubRepoCacheWarmer />;
}
