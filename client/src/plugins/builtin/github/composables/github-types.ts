export interface GitHubRepo {
  id: number;
  full_name: string;
  name: string;
  owner: {
    login: string;
    avatar_url: string;
  };
  description: string | null;
  html_url: string;
  private: boolean;
  stargazers_count: number;
  language: string | null;
  updated_at: string;
}

export interface CachedGitHubRepo {
  id: number;
  full_name: string;
  name: string;
  owner_login: string;
  private: boolean;
  language: string | null;
  stargazers_count: number;
}

export interface GitHubIssue {
  id: number;
  number: number;
  title: string;
  body: string | null;
  html_url: string;
  state: "open" | "closed";
  labels: readonly { readonly name: string; readonly color: string }[];
  user: { login: string; avatar_url: string };
  comments: number;
  created_at: string;
  updated_at: string;
  pull_request?: { url: string };
}

export interface GitHubPullRequest {
  id: number;
  number: number;
  title: string;
  body: string | null;
  html_url: string;
  state: "open" | "closed" | "merged";
  labels: readonly { readonly name: string; readonly color: string }[];
  user: { login: string; avatar_url: string };
  comments: number;
  additions: number;
  deletions: number;
  changed_files: number;
  head: { ref: string; sha: string };
  base: { ref: string; sha: string };
  created_at: string;
  updated_at: string;
  merged_at: string | null;
  draft: boolean;
}

export interface BookmarkedRepo {
  fullName: string;
  owner: string;
  name: string;
}

export interface GitHubLabel {
  name: string;
  color: string;
  description: string | null;
}

export interface GitHubMilestone {
  number: number;
  title: string;
  state: "open" | "closed";
  open_issues: number;
  closed_issues: number;
}

export interface GitHubAssignee {
  login: string;
  avatar_url: string;
}

export interface IssueFilterState {
  state: "open" | "closed" | "all";
  labels: string[];
  milestone: string | null;
  assignee: string | null;
  author: string | null;
  type: string | null;
  sort: "created" | "updated" | "comments";
  direction: "asc" | "desc";
  search: string;
}

export const DEFAULT_ISSUE_FILTER: IssueFilterState = {
  state: "open",
  labels: [],
  milestone: null,
  assignee: null,
  author: null,
  type: null,
  sort: "updated",
  direction: "desc",
  search: "",
};
