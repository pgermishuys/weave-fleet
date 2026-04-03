"use client";

import { useState, useEffect, useRef } from "react";
import { MarkdownRenderer } from "@/components/session/markdown-renderer";
import {
  Collapsible,
  CollapsibleTrigger,
  CollapsibleContent,
} from "@/components/ui/collapsible";
import { Badge } from "@/components/ui/badge";
import { ChevronRight, MessageSquare, GitBranch, Loader2 } from "lucide-react";
import { cn } from "@/lib/utils";
import { useGitHubComments } from "../hooks/use-github-comments";
import { CreateSessionButton } from "./create-session-button";
import type { GitHubPullRequest } from "../types";
import type { ContextSource } from "@/integrations/types";

interface PrRowProps {
  pr: GitHubPullRequest;
  owner: string;
  repo: string;
}

function formatAge(dateStr: string): string {
  const diff = Date.now() - new Date(dateStr).getTime();
  const minutes = Math.floor(diff / 60000);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  return `${days}d ago`;
}

export function PrRow({ pr, owner, repo }: PrRowProps) {
  const [isOpen, setIsOpen] = useState(false);
  const { comments, isLoading, fetch: fetchComments } =
    useGitHubComments(owner, repo, "pulls", pr.number);

  const hasFetchedRef = useRef(false);

  useEffect(() => {
    if (isOpen && !hasFetchedRef.current && !isLoading) {
      hasFetchedRef.current = true;
      fetchComments();
    }
  }, [isOpen, isLoading, fetchComments]);

  const contextSource: ContextSource = {
    type: "github-pr",
    url: pr.html_url,
    title: `PR #${pr.number}: ${pr.title}`,
    body: pr.body ?? "",
    metadata: {
      owner,
      repo,
      number: pr.number,
      labels: pr.labels,
      state: pr.state,
      additions: pr.additions,
      deletions: pr.deletions,
      changed_files: pr.changed_files,
      head: pr.head.ref,
      base: pr.base.ref,
      draft: pr.draft,
      comments: comments.map((c) => ({
        author: c.user.login,
        body: c.body,
        createdAt: c.created_at,
      })),
    },
  };

  return (
    <Collapsible open={isOpen} onOpenChange={setIsOpen}>
      <div className="flex items-center gap-3 px-3 py-2.5 rounded-md hover:bg-muted/50 transition-colors group">
        <CollapsibleTrigger className="flex items-center gap-3 flex-1 min-w-0 text-left">
          <ChevronRight
            className={cn(
              "h-3.5 w-3.5 shrink-0 text-muted-foreground transition-transform",
              isOpen && "rotate-90"
            )}
          />
          <span className="text-xs text-muted-foreground font-mono shrink-0">
            #{pr.number}
          </span>
          <span className="text-sm truncate">{pr.title}</span>
          <div className="flex items-center gap-1 shrink-0">
            {pr.draft && (
              <Badge variant="secondary" className="text-[10px]">
                Draft
              </Badge>
            )}
            {pr.labels.map((l) => (
              <Badge
                key={l.name}
                variant="outline"
                className="text-[10px] px-1.5 py-0"
                style={{ borderColor: `#${l.color}`, color: `#${l.color}` }}
              >
                {l.name}
              </Badge>
            ))}
          </div>
        </CollapsibleTrigger>
        <div className="flex items-center gap-2 shrink-0">
          <span className="text-xs font-mono">
            <span className="text-green-500">+{pr.additions}</span>{" "}
            <span className="text-red-500">-{pr.deletions}</span>
          </span>
          <Badge variant="outline" className="text-[10px] font-mono gap-1">
            <GitBranch className="h-2.5 w-2.5" />
            {pr.head.ref}
          </Badge>
          {pr.comments > 0 && (
            <span className="flex items-center gap-1 text-xs text-muted-foreground">
              <MessageSquare className="h-3 w-3" />
              {pr.comments}
            </span>
          )}
          <span className="text-xs text-muted-foreground">{pr.user.login}</span>
          <span className="text-xs text-muted-foreground">
            {formatAge(pr.created_at)}
          </span>
          <div className="opacity-0 group-hover:opacity-100 transition-opacity">
            <CreateSessionButton contextSource={contextSource} />
          </div>
        </div>
      </div>
      <CollapsibleContent>
        <div className="ml-9 mr-3 mb-3 p-4 rounded-md border bg-muted/30 space-y-3">
          {pr.body ? (
            <MarkdownRenderer content={pr.body} />
          ) : (
            <p className="text-sm text-muted-foreground italic">
              No description provided.
            </p>
          )}

          <div className="text-xs text-muted-foreground">
            <span className="font-medium">
              {pr.additions + pr.deletions} lines changed
            </span>{" "}
            across {pr.changed_files} file{pr.changed_files !== 1 ? "s" : ""}
            &nbsp;·&nbsp;
            <span className="text-green-500">+{pr.additions}</span>{" "}
            <span className="text-red-500">-{pr.deletions}</span>
          </div>

          <div className="border-t pt-3 space-y-2">
            <p className="text-xs font-medium text-muted-foreground">
              Comments ({pr.comments})
            </p>
            {isLoading ? (
              <div className="flex items-center gap-2 text-xs text-muted-foreground">
                <Loader2 className="h-3.5 w-3.5 animate-spin" />
                Loading comments…
              </div>
            ) : comments.length > 0 ? (
              <div className="space-y-3">
                {comments.map((comment) => (
                  <div key={comment.id} className="text-xs space-y-1">
                    <div className="flex items-center gap-2">
                      <span className="font-medium">@{comment.user.login}</span>
                      <span className="text-muted-foreground">
                        {formatAge(comment.created_at)}
                      </span>
                    </div>
                    <MarkdownRenderer content={comment.body} className="text-muted-foreground" />
                  </div>
                ))}
              </div>
            ) : (
              <p className="text-xs text-muted-foreground">No comments yet.</p>
            )}
          </div>

          <div className="border-t pt-3">
            <CreateSessionButton contextSource={contextSource} />
          </div>
        </div>
      </CollapsibleContent>
    </Collapsible>
  );
}
