"use client";

import { Tabs, TabsList, TabsTrigger, TabsContent } from "@/components/ui/tabs";
import { Badge } from "@/components/ui/badge";
import { CircleDot, GitPullRequest } from "lucide-react";
import { RepoSelector } from "./components/repo-selector";
import { IssueList } from "./components/issue-list";
import { PrList } from "./components/pr-list";
import { usePersistedState } from "@/hooks/use-persisted-state";
import { useIntegrationsContext } from "@/contexts/integrations-context";
import { useGitHubIssues } from "./hooks/use-github-issues";
import { useGitHubPulls } from "./hooks/use-github-pulls";
import { DEFAULT_ISSUE_FILTER, type CachedGitHubRepo } from "./types";
import { GITHUB_LAST_REPO_KEY } from "./storage";

function GitHubBrowserInner({ repo }: { repo: CachedGitHubRepo }) {
  const [owner, repoName] = repo.full_name.split("/");
  const { issues } = useGitHubIssues(owner, repoName, DEFAULT_ISSUE_FILTER);
  const { pulls } = useGitHubPulls(owner, repoName, { state: "open" });

  return (
    <Tabs defaultValue="issues">
      <TabsList variant="line">
        <TabsTrigger value="issues" className="gap-1.5">
          <CircleDot className="h-3.5 w-3.5" />
          Issues
          {issues.length > 0 && (
            <Badge variant="secondary" className="text-[10px] ml-1">
              {issues.length}
            </Badge>
          )}
        </TabsTrigger>
        <TabsTrigger value="pulls" className="gap-1.5">
          <GitPullRequest className="h-3.5 w-3.5" />
          Pull Requests
          {pulls.length > 0 && (
            <Badge variant="secondary" className="text-[10px] ml-1">
              {pulls.length}
            </Badge>
          )}
        </TabsTrigger>
      </TabsList>

      <TabsContent value="issues" className="mt-4">
        <IssueList owner={owner} repo={repoName} />
      </TabsContent>

      <TabsContent value="pulls" className="mt-4">
        <PrList owner={owner} repo={repoName} />
      </TabsContent>
    </Tabs>
  );
}

export function GitHubBrowser() {
  const { connectedIntegrations } = useIntegrationsContext();
  const isGitHubConnected = connectedIntegrations.some((i) => i.id === "github");
  const [selectedRepo, setSelectedRepo] =
    usePersistedState<CachedGitHubRepo | null>(GITHUB_LAST_REPO_KEY, null);

  if (!isGitHubConnected) {
    return (
      <div className="flex flex-col items-center justify-center py-16 gap-2 text-center">
        <p className="text-sm text-muted-foreground">GitHub is not connected.</p>
        <p className="text-xs text-muted-foreground">Connect GitHub in Settings to browse repositories.</p>
      </div>
    );
  }

  return (
    <div>
      {/* Repo selector bar */}
      <div className="mb-4">
        <RepoSelector selected={selectedRepo} onSelect={setSelectedRepo} />
      </div>

      {selectedRepo ? (
        <GitHubBrowserInner repo={selectedRepo} />
      ) : (
        <div className="flex flex-col items-center justify-center py-16 gap-2 text-center">
          <p className="text-sm text-muted-foreground">
            Select a repository to browse issues and pull requests.
          </p>
        </div>
      )}
    </div>
  );
}

export default GitHubBrowser;
