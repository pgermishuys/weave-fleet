import { NextRequest, NextResponse } from "next/server";
import { getGitHubToken, githubFetch } from "../../../../_lib/github-fetch";
import type { GitHubMilestone } from "@/integrations/github/types";

// GET /api/integrations/github/repos/[owner]/[repo]/milestones
export async function GET(
  _request: NextRequest,
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

  const result = await githubFetch<GitHubMilestone[]>(
    `/repos/${owner}/${repo}/milestones`,
    token,
    { params: { state: "open", per_page: 100 } }
  );

  if (result.error) {
    return NextResponse.json({ error: result.error }, { status: result.status });
  }

  return NextResponse.json(result.data ?? [], { status: 200 });
}
