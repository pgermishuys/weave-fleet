import { NextRequest, NextResponse } from "next/server";
import { getGitHubToken, githubFetch } from "../../../../../../_lib/github-fetch";
import type { GitHubComment } from "@/integrations/github/types";

// GET /api/integrations/github/repos/[owner]/[repo]/pulls/[number]/comments
export async function GET(
  request: NextRequest,
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
  const { searchParams } = new URL(request.url);

  const result = await githubFetch<GitHubComment[]>(
    `/repos/${owner}/${repo}/pulls/${number}/comments`,
    token,
    {
      params: {
        page: searchParams.get("page") ?? "1",
        per_page: searchParams.get("per_page") ?? "50",
      },
    }
  );

  if (result.error) {
    return NextResponse.json({ error: result.error }, { status: result.status });
  }

  return NextResponse.json(result.data, { status: 200 });
}
