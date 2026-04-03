import { NextRequest, NextResponse } from "next/server";
import { getGitHubToken, githubFetch } from "../../../../_lib/github-fetch";
import type { GitHubLabel } from "@/integrations/github/types";

// GET /api/integrations/github/repos/[owner]/[repo]/labels
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

  // Fetch all pages (labels are typically <100 per repo)
  const allLabels: GitHubLabel[] = [];
  let page = 1;

  while (true) {
    const result = await githubFetch<GitHubLabel[]>(
      `/repos/${owner}/${repo}/labels`,
      token,
      { params: { per_page: 100, page } }
    );

    if (result.error) {
      return NextResponse.json({ error: result.error }, { status: result.status });
    }

    const labels = result.data ?? [];
    allLabels.push(...labels);

    if (labels.length < 100) break;
    page++;
  }

  return NextResponse.json(allLabels, { status: 200 });
}
