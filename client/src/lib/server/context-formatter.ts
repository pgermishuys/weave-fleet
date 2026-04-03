import type { ContextSource } from "@/integrations/types";

interface LabelItem {
  name: string;
  color?: string;
}

interface CommentItem {
  author: string;
  body: string;
  createdAt: string;
}

function formatAge(dateStr: string): string {
  const diff = Date.now() - new Date(dateStr).getTime();
  const minutes = Math.floor(diff / 60000);
  if (minutes < 60) return `${minutes} minute${minutes !== 1 ? "s" : ""} ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} hour${hours !== 1 ? "s" : ""} ago`;
  const days = Math.floor(hours / 24);
  return `${days} day${days !== 1 ? "s" : ""} ago`;
}

function formatLabels(labels: unknown): string {
  if (!Array.isArray(labels) || labels.length === 0) return "None";
  return (labels as LabelItem[]).map((l) => l.name).join(", ");
}

function formatComments(comments: unknown): string {
  if (!Array.isArray(comments) || comments.length === 0) return "";
  return (comments as CommentItem[])
    .map((c) => {
      const age = formatAge(c.createdAt);
      return `**@${c.author}** (${age}):\n${c.body}`;
    })
    .join("\n\n---\n\n");
}

/**
 * Converts a ContextSource into a structured markdown prompt for the AI agent.
 * This is a pure function — unit-testable with no side effects.
 */
export function formatContextAsPrompt(context: ContextSource): string {
  const meta = context.metadata as Record<string, unknown>;

  if (context.type === "github-issue") {
    const owner = meta.owner as string | undefined;
    const repo = meta.repo as string | undefined;
    const repoLabel = owner && repo ? `${owner}/${repo}` : "";
    const state = (meta.state as string | undefined) ?? "unknown";
    const labels = formatLabels(meta.labels);
    const commentsSection = formatComments(meta.comments);

    const parts: string[] = [
      `# Context: GitHub Issue`,
      ``,
      `**Issue**: [${context.title}](${context.url})`,
      ...(repoLabel ? [`**Repository**: ${repoLabel}`] : []),
      `**State**: ${state}`,
      `**Labels**: ${labels}`,
      ``,
      `## Description`,
      ``,
      context.body.trim() || "_No description provided._",
    ];

    if (commentsSection) {
      parts.push(``, `## Comments`, ``, commentsSection);
    }

    parts.push(
      ``,
      `---`,
      ``,
      `This issue has been loaded as context. What would you like to do?`
    );

    return parts.join("\n");
  }

  if (context.type === "github-pr") {
    const owner = meta.owner as string | undefined;
    const repo = meta.repo as string | undefined;
    const repoLabel = owner && repo ? `${owner}/${repo}` : "";
    const state = (meta.state as string | undefined) ?? "unknown";
    const head = (meta.head as string | undefined) ?? "";
    const base = (meta.base as string | undefined) ?? "";
    const additions = (meta.additions as number | undefined) ?? 0;
    const deletions = (meta.deletions as number | undefined) ?? 0;
    const changedFiles = (meta.changed_files as number | undefined) ?? 0;
    const draft = meta.draft as boolean | undefined;
    const labels = formatLabels(meta.labels);
    const commentsSection = formatComments(meta.comments);

    const parts: string[] = [
      `# Context: GitHub Pull Request`,
      ``,
      `**PR**: [${context.title}](${context.url})`,
      ...(repoLabel ? [`**Repository**: ${repoLabel}`] : []),
      ...(head && base ? [`**Branch**: \`${head}\` → \`${base}\``] : []),
      `**State**: ${draft ? "draft" : state}`,
      `**Changes**: +${additions} -${deletions} across ${changedFiles} file${changedFiles !== 1 ? "s" : ""}`,
      `**Labels**: ${labels}`,
      ``,
      `## Description`,
      ``,
      context.body.trim() || "_No description provided._",
    ];

    if (commentsSection) {
      parts.push(``, `## Comments`, ``, commentsSection);
    }

    parts.push(
      ``,
      `---`,
      ``,
      `This pull request has been loaded as context. What would you like to do?`
    );

    return parts.join("\n");
  }

  // Generic fallback for unknown types
  const metaEntries = Object.entries(meta)
    .map(([k, v]) => `- **${k}**: ${JSON.stringify(v)}`)
    .join("\n");

  return [
    `# Context: ${context.title}`,
    ``,
    `**URL**: ${context.url}`,
    `**Type**: ${context.type}`,
    ``,
    `## Content`,
    ``,
    context.body.trim() || "_No content provided._",
    ...(metaEntries ? [``, `## Metadata`, ``, metaEntries] : []),
    ``,
    `---`,
    ``,
    `This context has been loaded. What would you like to do?`,
  ].join("\n");
}
