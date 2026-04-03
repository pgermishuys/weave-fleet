"use client";

import { useState, useEffect, useRef } from "react";
import { MarkdownRenderer } from "@/components/session/markdown-renderer";
import {
  Collapsible,
  CollapsibleTrigger,
  CollapsibleContent,
} from "@/components/ui/collapsible";
import { Badge } from "@/components/ui/badge";
import { ChevronRight, MessageSquare, Loader2, Github } from "lucide-react";
import { cn } from "@/lib/utils";
import { useGitHubComments } from "../hooks/use-github-comments";
import { CreateSessionButton } from "./create-session-button";
import type { GitHubIssue } from "../types";
import type { ContextSource } from "@/integrations/types";

interface IssueRowProps {
  issue: GitHubIssue;
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

export function IssueRow({ issue, owner, repo }: IssueRowProps) {
  const [isOpen, setIsOpen] = useState(false);
  const { comments, isLoading, fetch: fetchComments } =
    useGitHubComments(owner, repo, "issues", issue.number);

  const hasFetchedRef = useRef(false);

  useEffect(() => {
    if (isOpen && !hasFetchedRef.current && !isLoading) {
      hasFetchedRef.current = true;
      fetchComments();
    }
  }, [isOpen, isLoading, fetchComments]);

  const contextSource: ContextSource = {
    type: "github-issue",
    url: issue.html_url,
    title: `Issue #${issue.number}: ${issue.title}`,
    body: issue.body ?? "",
    metadata: {
      owner,
      repo,
      number: issue.number,
      labels: issue.labels,
      state: issue.state,
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
            #{issue.number}
          </span>
          <span className="text-sm truncate">{issue.title}</span>
          <div className="flex items-center gap-1 shrink-0">
            {issue.labels.map((l) => (
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
          {issue.comments > 0 && (
            <span className="flex items-center gap-1 text-xs text-muted-foreground">
              <MessageSquare className="h-3 w-3" />
              {issue.comments}
            </span>
          )}
          <span className="text-xs text-muted-foreground">
            {issue.user.login}
          </span>
          <span className="text-xs text-muted-foreground">
            {formatAge(issue.created_at)}
          </span>
          <div className="opacity-0 group-hover:opacity-100 transition-opacity flex items-center gap-1">
            <a
              href={issue.html_url}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center justify-center h-7 w-7 rounded-md text-muted-foreground hover:text-foreground hover:bg-muted transition-colors"
              aria-label="Open on GitHub"
              onClick={(e) => e.stopPropagation()}
            >
              <Github className="h-3.5 w-3.5" />
            </a>
            <CreateSessionButton contextSource={contextSource} />
          </div>
        </div>
      </div>
      <CollapsibleContent>
        <div className="ml-9 mr-3 mb-3 p-4 rounded-md border bg-muted/30 space-y-3">
          {issue.body ? (
            <MarkdownRenderer content={issue.body} />
          ) : (
            <p className="text-sm text-muted-foreground italic">
              No description provided.
            </p>
          )}

          <div className="border-t pt-3 space-y-2">
            <p className="text-xs font-medium text-muted-foreground">
              Comments ({issue.comments})
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
