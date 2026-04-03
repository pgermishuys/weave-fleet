"use client";

import Link from "next/link";
import { Github } from "lucide-react";
import { Header } from "@/components/layout/header";
import { useIntegrationsContext } from "@/contexts/integrations-context";
import { useBookmarkedRepos } from "@/integrations/github/hooks/use-bookmarked-repos";

export default function GitHubPage() {
  const { connectedIntegrations } = useIntegrationsContext();
  const isGitHubConnected = connectedIntegrations.some((i) => i.id === "github");
  const { repos } = useBookmarkedRepos();

  return (
    <div className="flex flex-col h-full">
      <Header
        title="GitHub"
        subtitle="Browse issues and pull requests for your repositories"
      />
      <div className="flex-1 overflow-auto thin-scrollbar p-3 sm:p-4 lg:p-6">
        {!isGitHubConnected ? (
          <div className="flex flex-col items-center justify-center h-full gap-3 text-center">
            <Github className="h-10 w-10 text-muted-foreground/40" />
            <p className="text-sm text-muted-foreground">GitHub is not connected.</p>
            <p className="text-xs text-muted-foreground/70">
              Connect GitHub in Settings to browse repositories.
            </p>
          </div>
        ) : repos.length === 0 ? (
          <div className="flex flex-col items-center justify-center h-full gap-3 text-center">
            <Github className="h-10 w-10 text-muted-foreground/40" />
            <p className="text-sm text-muted-foreground">
              No repositories added yet.
            </p>
            <p className="text-xs text-muted-foreground/70">
              Click <span className="font-mono">+</span> in the GitHub panel to add a repository.
            </p>
          </div>
        ) : (
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
            {repos.map((repo) => (
              <Link
                key={repo.fullName}
                href={`/github/${repo.owner}/${repo.name}`}
                className="group flex flex-col gap-1 rounded-lg border border-border bg-card p-4 hover:bg-accent/50 transition-colors"
              >
                <div className="flex items-center gap-2">
                  <Github className="h-4 w-4 shrink-0 text-muted-foreground" />
                  <span className="text-sm font-medium truncate">{repo.fullName}</span>
                </div>
              </Link>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
