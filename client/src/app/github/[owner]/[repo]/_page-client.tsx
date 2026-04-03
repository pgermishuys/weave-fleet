"use client";

import { usePathname } from "next/navigation";
import { Badge } from "@/components/ui/badge";
import { CircleDot, GitPullRequest } from "lucide-react";
import { Tabs, TabsList, TabsTrigger, TabsContent } from "@/components/ui/tabs";
import { Header } from "@/components/layout/header";
import { useIntegrationsContext } from "@/contexts/integrations-context";
import { IssueList } from "@/integrations/github/components/issue-list";
import { PrList } from "@/integrations/github/components/pr-list";
import { useGitHubIssues } from "@/integrations/github/hooks/use-github-issues";
import { useGitHubPulls } from "@/integrations/github/hooks/use-github-pulls";
import { DEFAULT_ISSUE_FILTER } from "@/integrations/github/types";

export default function GitHubRepoPage() {
  const pathname = usePathname();
  // Parse owner/repo from URL (/github/{owner}/{repo}) instead of server
  // params, which contain the template placeholder "_" from the RSC payload.
  const segments = pathname.split("/").filter(Boolean);
  const owner = decodeURIComponent(segments[1] ?? "");
  const repo = decodeURIComponent(segments[2] ?? "");
  const { connectedIntegrations } = useIntegrationsContext();
  const isGitHubConnected = connectedIntegrations.some((i) => i.id === "github");

  const ownerValue = isGitHubConnected ? owner : null;
  const repoValue = isGitHubConnected ? repo : null;
  const { issues: openIssues } = useGitHubIssues(ownerValue, repoValue, DEFAULT_ISSUE_FILTER);
  const { pulls: openPulls } = useGitHubPulls(ownerValue, repoValue, { state: "open" });

  return (
    <div className="flex flex-col h-full">
      <Header title={`${owner}/${repo}`} />
      <div className="flex-1 overflow-auto thin-scrollbar p-3 sm:p-4 lg:p-6">
        {!isGitHubConnected ? (
          <div className="flex flex-col items-center justify-center h-full gap-3 text-center">
            <p className="text-sm text-muted-foreground">GitHub is not connected.</p>
            <p className="text-xs text-muted-foreground/70">
              Connect GitHub in Settings to browse this repository.
            </p>
          </div>
        ) : (
        <Tabs defaultValue="issues">
          <TabsList variant="line">
            <TabsTrigger value="issues" className="gap-1.5">
              <CircleDot className="h-3.5 w-3.5" />
              Issues
              {openIssues.length > 0 && (
                <Badge variant="secondary" className="text-[10px] ml-1">
                  {openIssues.length}
                </Badge>
              )}
            </TabsTrigger>
            <TabsTrigger value="pulls" className="gap-1.5">
              <GitPullRequest className="h-3.5 w-3.5" />
              Pull Requests
              {openPulls.length > 0 && (
                <Badge variant="secondary" className="text-[10px] ml-1">
                  {openPulls.length}
                </Badge>
              )}
            </TabsTrigger>
          </TabsList>

          <TabsContent value="issues" className="mt-4">
            <IssueList owner={owner} repo={repo} />
          </TabsContent>

          <TabsContent value="pulls" className="mt-4">
            <PrList owner={owner} repo={repo} />
          </TabsContent>
        </Tabs>
        )}
      </div>
    </div>
  );
}
