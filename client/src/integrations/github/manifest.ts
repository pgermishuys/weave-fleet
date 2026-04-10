import { lazy } from "react";
import { Github } from "lucide-react";
import type { IntegrationManifest, ContextSource } from "@/integrations/types";
import type { SessionSourceSelection } from "@/lib/api-types";
import type {
  GitHubIssue,
  GitHubPullRequest,
  GitHubComment,
} from "./types";

// Module-level flag updated by IntegrationsContext on each poll
// Allows isConfigured() to remain synchronous
let _isGitHubConfigured = false;

export function setGitHubConfigured(value: boolean): void {
  _isGitHubConfigured = value;
}

async function resolveContext(url: string): Promise<ContextSource | null> {
  // Parse GitHub issue/PR URLs
  // Matches: github.com/:owner/:repo/issues/:number
  //          github.com/:owner/:repo/pull/:number
  const issueMatch = url.match(
    /github\.com\/([^/]+)\/([^/]+)\/issues\/(\d+)/
  );
  const prMatch = url.match(/github\.com\/([^/]+)\/([^/]+)\/pull\/(\d+)/);

  if (!issueMatch && !prMatch) {
    return null;
  }

  const match = issueMatch ?? prMatch!;
  const [, owner, repo, numberStr] = match;
  const number = parseInt(numberStr, 10);
  const isPR = !!prMatch;

  const basePath = isPR
    ? `/api/integrations/github/repos/${owner}/${repo}/pulls/${number}`
    : `/api/integrations/github/repos/${owner}/${repo}/issues/${number}`;

  const commentsPath = `${basePath}/comments`;

  try {
    const [itemRes, commentsRes] = await Promise.all([
      fetch(basePath),
      fetch(commentsPath),
    ]);

    if (!itemRes.ok) return null;

    const item = (await itemRes.json()) as GitHubIssue | GitHubPullRequest;
    const comments: GitHubComment[] = commentsRes.ok
      ? ((await commentsRes.json()) as GitHubComment[])
      : [];

    if (isPR) {
      const pr = item as GitHubPullRequest;
      const source = buildGitHubSource("github-pull-request", owner, repo, pr.number);

      return {
        type: "github-pr",
        url,
        title: pr.title,
        source,
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
          comments,
        },
      };
    } else {
      const issue = item as GitHubIssue;
      const source = buildGitHubSource("github-issue", owner, repo, issue.number);

      return {
        type: "github-issue",
        url,
        title: issue.title,
        source,
        metadata: {
          owner,
          repo,
          number: issue.number,
          labels: issue.labels,
          state: issue.state,
          comments,
        },
      };
    }
  } catch {
    return null;
  }
}

function buildGitHubSource(sourceType: "github-issue" | "github-pull-request", owner: string, repo: string, number: number): SessionSourceSelection {
  return {
    key: {
      providerId: "builtin.github",
      sourceType,
      actionId: "add-to-session",
      contractVersion: 1,
    },
    input: {
      owner,
      repo,
      number,
    },
  };
}

export const githubManifest: IntegrationManifest = {
  id: "github",
  name: "GitHub",
  icon: Github,
  browserComponent: lazy(() =>
    import("./browser").then((m) => ({ default: m.GitHubBrowser }))
  ),
  settingsComponent: lazy(() =>
    import("./settings").then((m) => ({ default: m.GitHubSettings }))
  ),
  isConfigured: () => _isGitHubConfigured,
  resolveContext,
  pluginDescriptor: {
    hasBackend: true,
  },
};
