import { NextRequest, NextResponse } from "next/server";
import { getGitHubToken, githubFetch } from "../../../../_lib/github-fetch";
import type { GitHubIssue } from "@/integrations/github/types";

// GET /api/integrations/github/repos/[owner]/[repo]/issues
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

  // Only forward optional filter params when they are present
  const labels = searchParams.get("labels") ?? undefined;
  const milestone = searchParams.get("milestone") ?? undefined;
  const assignee = searchParams.get("assignee") ?? undefined;
  // "author" in the filter state maps to "creator" in the GitHub API
  const creator = searchParams.get("creator") ?? undefined;
  const type = searchParams.get("type") ?? undefined;

  const result = await githubFetch<GitHubIssue[]>(
    `/repos/${owner}/${repo}/issues`,
    token,
    {
      params: {
        state: searchParams.get("state") ?? "open",
        page: searchParams.get("page") ?? "1",
        per_page: searchParams.get("per_page") ?? "30",
        sort: searchParams.get("sort") ?? "updated",
        direction: searchParams.get("direction") ?? undefined,
        // Optional filter params — only forwarded when present
        labels,
        milestone,
        assignee,
        creator,
        type,
      },
    }
  );

  if (result.error) {
    return NextResponse.json({ error: result.error }, { status: result.status });
  }

  return NextResponse.json(result.data, { status: 200 });
}
