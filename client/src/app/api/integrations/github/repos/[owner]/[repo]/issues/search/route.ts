import { NextRequest, NextResponse } from "next/server";
import { getGitHubToken, githubFetch } from "../../../../../_lib/github-fetch";
import type { GitHubIssue } from "@/integrations/github/types";

interface GitHubSearchResult {
  total_count: number;
  incomplete_results: boolean;
  items: GitHubIssue[];
}

// GET /api/integrations/github/repos/[owner]/[repo]/issues/search?q=...&page=1&per_page=30
export async function GET(
  request: NextRequest,
  { params }: { params: Promise<{ owner: string; repo: string }> }
): Promise<NextResponse> {
  const token = getGitHubToken();
  if (!token) {
    return NextResponse.json(
      { error: "GitHub not connected" },
      { status: 401 }
    );
  }

  const { owner, repo } = await params;
  const { searchParams } = new URL(request.url);

  const q = searchParams.get("q") ?? "";
  const page = searchParams.get("page") ?? "1";
  const per_page = searchParams.get("per_page") ?? "30";

  // Construct the full search query server-side:
  // Always scope to this repo and restrict to issues (not PRs)
  const fullQuery = `repo:${owner}/${repo} type:issue ${q}`.trim();

  const result = await githubFetch<GitHubSearchResult>(
    "/search/issues",
    token,
    {
      params: {
        q: fullQuery,
        page,
        per_page,
      },
    }
  );

  if (result.error) {
    // 422 means invalid query — return a helpful error rather than 500
    if (result.status === 422) {
      return NextResponse.json(
        { error: "Invalid search query", total_count: 0, items: [] },
        { status: 422 }
      );
    }
    return NextResponse.json({ error: result.error }, { status: result.status });
  }

  return NextResponse.json(result.data, { status: 200 });
}
