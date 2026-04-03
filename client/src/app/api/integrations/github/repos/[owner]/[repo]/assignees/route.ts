import { NextRequest, NextResponse } from "next/server";
import { getGitHubToken, githubFetch } from "../../../../_lib/github-fetch";
import type { GitHubAssignee } from "@/integrations/github/types";

// GET /api/integrations/github/repos/[owner]/[repo]/assignees
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

  // Fetch all pages
  const allAssignees: GitHubAssignee[] = [];
  let page = 1;

  while (true) {
    const result = await githubFetch<GitHubAssignee[]>(
      `/repos/${owner}/${repo}/assignees`,
      token,
      { params: { per_page: 100, page } }
    );

    if (result.error) {
      return NextResponse.json({ error: result.error }, { status: result.status });
    }

    const assignees = result.data ?? [];
    allAssignees.push(...assignees);

    if (assignees.length < 100) break;
    page++;
  }

  return NextResponse.json(allAssignees, { status: 200 });
}
