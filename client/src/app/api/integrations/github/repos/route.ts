import { NextRequest, NextResponse } from "next/server";
import { getGitHubToken, githubFetch } from "../_lib/github-fetch";
import type { GitHubRepo } from "@/integrations/github/types";

// GET /api/integrations/github/repos
export async function GET(request: NextRequest): Promise<NextResponse> {
  const token = getGitHubToken();
  if (!token) {
    return NextResponse.json(
      { error: "GitHub not connected" },
      { status: 401 }
    );
  }

  const { searchParams } = new URL(request.url);
  const result = await githubFetch<GitHubRepo[]>("/user/repos", token, {
    params: {
      page: searchParams.get("page") ?? "1",
      per_page: searchParams.get("per_page") ?? "30",
      sort: searchParams.get("sort") ?? "updated",
      direction: searchParams.get("direction") ?? undefined,
    },
  });

  if (result.error) {
    return NextResponse.json({ error: result.error }, { status: result.status });
  }

  return NextResponse.json(result.data, { status: 200 });
}
