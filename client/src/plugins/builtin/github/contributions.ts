import { Github } from "lucide-vue-next";
import { computed } from "vue";
import type { FleetPluginBoardSource, FleetPluginRepositorySource } from "@/plugins/types";
import { useGitHubBookmarks } from "./composables/use-github-bookmarks";
import { useGitHubRepos } from "./composables/use-github-repos";

/** Your GitHub repositories, for the folder picker's Clone view. Nothing loads until the view asks. */
export const githubRepositorySource: FleetPluginRepositorySource = {
  id: "github",
  icon: Github,
  useRepositories: () => {
    const { repos, refresh } = useGitHubRepos({ autoLoad: false });
    return {
      repos: computed(() => repos.value.map((repo) => ({ id: repo.id, fullName: repo.full_name, name: repo.name }))),
      refresh: () => refresh(),
    };
  },
};

/** Your bookmarked GitHub repositories, as places a board syncs issues from. */
export const githubBoardSource: FleetPluginBoardSource = {
  id: "github",
  description: "Add GitHub issue sources from your bookmarks, optionally narrowing sync to matching labels.",
  emptyHint: "Bookmark a repository in the GitHub panel to add it as a board source.",
  useRepositories: () => {
    const { bookmarks, error, isLoading, refresh } = useGitHubBookmarks();
    return { repositories: bookmarks, error, isLoading, refresh };
  },
};
