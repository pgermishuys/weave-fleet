import { NextRequest, NextResponse } from "next/server";
import { getGitHubToken, githubFetch } from "../../../../../_lib/github-fetch";
import type { GitHubIssue } from "@/integrations/github/types";

// GET /api/integrations/github/repos/[owner]/[repo]/issues/[number]
export async function GET(
  _request: NextRequest,
  {
    params,
  }: { params: Promise<{ owner: string; repo: string; number: string }> }
): Promise<NextResponse> {
  const token = getGitHubToken();
  if (!token) {
    return NextResponse.json(
      { error: "GitHub not connected" },
      { status: 401 }
    );
  }

  const { owner, repo, number } = await params;

  const result = await githubFetch<GitHubIssue>(
    `/repos/${owner}/${repo}/issues/${number}`,
    token
  );

  if (result.error) {
    return NextResponse.json({ error: result.error }, { status: result.status });
  }

  return NextResponse.json(result.data, { status: 200 });
}
